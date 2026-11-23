using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Highball.World;

/// Writes an object graph as it is in memory and reads it back: every instance field of every object reached, references kept
/// (an object reached twice is written once and read back as one), so whatever the graph holds comes back without a list of
/// what to keep. Only for reading back by the same build of the game: values with no references in them (numbers, enums,
/// tuples of them, structs of them) are copied as their bytes are laid out. Lists, dictionaries and sets are written as their
/// items, not their insides; a collection with a comparer other than the default, a delegate, or anything holding the world
/// outside (a stream, a thread) isn't written: it throws, naming where.
internal static class GraphSnapshot
{
    private const byte Null = 0, Seen = 1, New = 2, Boxed = 3, External = 4;

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();
    private static readonly ConcurrentDictionary<Type, bool> Blittable = new();
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo> Generic = new();

    internal static bool IsBlittable(Type type) => Blittable.GetOrAdd(type, static t =>
        t.IsValueType && !(bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(t).Invoke(null, null)!);

    /// A type's layout as this build has it: its name and every instance field's name and type, all the way down through the value
    /// types it holds (which are copied as their bytes are laid out). Two builds that agree on this for every type in a snapshot read
    /// each other's snapshots; a build that doesn't would read garbage, so a snapshot kept across builds (CellStore) checks it first.
    internal static ulong LayoutOf(Type type)
    {
        var hash = new WordHash();
        void Add(string s)
        {
            hash.Add(s.Length);
            foreach (var c in s)
                hash.Add((int)c);
        }

        void Walk(Type t, int depth)
        {
            Add(NameOf(t));
            if (t.IsArray)
            {
                Walk(t.GetElementType()!, depth + 1);
                return;
            }

            if (depth > 8 || (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && t.Assembly != typeof(GraphSnapshot).Assembly && !t.IsGenericType))
                return;
            if (t.IsGenericType && t.Assembly != typeof(GraphSnapshot).Assembly)
            {
                // (A framework collection or tuple: written as its items, or its fields, whose types are what matter.)
                foreach (var arg in t.GetGenericArguments())
                    if (arg.IsValueType)
                        Walk(arg, depth + 1);
                if (!t.IsValueType)
                    return;
            }

            foreach (var f in FieldsOf(t))
            {
                Add(f.Name);
                Add(NameOf(f.FieldType));
                if (f.FieldType.IsValueType && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum)
                    Walk(f.FieldType, depth + 1);
                else if (f.FieldType.IsEnum)
                    Add(Enum.GetUnderlyingType(f.FieldType).Name);
            }
        }

        Walk(type, 0);
        return hash.Value;
    }

    /// A type's name without its assemblies' versions (which a version bump changes for no reason that matters here).
    internal static string NameOf(Type t) =>
        t.IsArray ? NameOf(t.GetElementType()!) + "[]" :
        t.IsGenericType && !t.IsGenericTypeDefinition ? NameOf(t.GetGenericTypeDefinition()) + "[" + string.Join(",", t.GetGenericArguments().Select(NameOf)) + "]" :
        t.FullName ?? t.Name;

    internal static FieldInfo[] FieldsOf(Type type) => Fields.GetOrAdd(type, static t =>
    {
        var all = new List<FieldInfo>();
        for (var at = t; at is not null && at != typeof(object) && at != typeof(ValueType); at = at.BaseType)
            all.AddRange(at.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        return [.. all];
    });

    private static MethodInfo Method(string name, params Type[] args) =>
        Generic.GetOrAdd((args.Length == 1 ? args[0] : typeof(ValueTuple<,>).MakeGenericType(args), name),
            _ => typeof(GraphSnapshot).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(args));

    private static bool IsDefaultComparer(object? comparer, Type of) =>
        comparer is null || ReferenceEquals(comparer, typeof(EqualityComparer<>).MakeGenericType(of).GetProperty("Default")!.GetValue(null)) ||
        comparer.GetType().Name.StartsWith("GenericEqualityComparer", StringComparison.Ordinal) ||
        comparer.GetType().Name.StartsWith("ObjectEqualityComparer", StringComparison.Ordinal) ||
        comparer.GetType().Name.StartsWith("EnumEqualityComparer", StringComparison.Ordinal) ||
        comparer.GetType().Name.StartsWith("NonRandomizedStringEqualityComparer", StringComparison.Ordinal);

    // --- Writing. ---

    /// external: objects written elsewhere (another cell's, see CellStore), written as a reference only, (kind, index), for the reader's
    /// resolve to find again.
    public sealed class Writer(BinaryWriter w, Func<FieldInfo, bool>? skip = null, Func<object, (int Kind, int Index)?>? external = null)
    {
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Type, int> _types = [];
        private readonly Stack<string> _path = new();
        public BinaryWriter Out => w;
        public int Objects => _ids.Count;
        /// Every object written whole, and every type named.
        public IEnumerable<object> Written => _ids.Keys;
        public IEnumerable<Type> Types => _types.Keys;

        public void WriteRoot(object root) => WriteRef(root);

        /// A value of the declared type, as a field of it would be written.
        public void Write(Type declared, object? value) => WriteValue(declared, value);

        /// An object's fields, without the object itself (the reader has it made already: ReadInto).
        public void WriteFields(object value)
        {
            foreach (var f in FieldsOf(value.GetType()))
                WriteField(f, value);
        }

        private void WriteType(Type type)
        {
            if (_types.TryGetValue(type, out var id))
            {
                w.Write(id);
                return;
            }

            _types[type] = _types.Count;
            w.Write(-1);
            w.Write(type.AssemblyQualifiedName!);
        }

        internal void WriteValue(Type declared, object? value)
        {
            if (declared.IsValueType)
            {
                if (Nullable.GetUnderlyingType(declared) is { } inner)
                {
                    w.Write(value is not null);
                    if (value is not null)
                        WriteValue(inner, value);
                    return;
                }

                if (WritePrimitive(w, declared, value!))
                    return;
                if (IsBlittable(declared))
                {
                    Method(nameof(WriteBlit), declared).Invoke(null, [w, value]);
                    return;
                }

                foreach (var f in FieldsOf(declared))
                    WriteField(f, value!);
                return;
            }

            WriteRef(value);
        }

        private void WriteField(FieldInfo f, object owner)
        {
            if (skip?.Invoke(f) == true)
                return;
            _path.Push(f.Name);
            WriteValue(f.FieldType, f.GetValue(owner));
            _path.Pop();
        }

        private void WriteRef(object? value)
        {
            if (value is null)
            {
                w.Write(Null);
                return;
            }

            var type = value.GetType();
            if (type.IsValueType)
            {
                w.Write(Boxed);
                WriteType(type);
                WriteValue(type, value);
                return;
            }

            if (_ids.TryGetValue(value, out var id))
            {
                w.Write(Seen);
                w.Write(id);
                return;
            }

            if (external?.Invoke(value) is { } far)
            {
                w.Write(External);
                w.Write(far.Kind);
                w.Write(far.Index);
                return;
            }

            _ids[value] = _ids.Count;
            w.Write(New);
            WriteType(type);
            WritePayload(type, value);
        }

        private NotSupportedException Unsupported(Type type, string why) =>
            new($"Graph snapshot: {type} at {string.Join(".", _path.Reverse())}: {why}");

        private void WritePayload(Type type, object value)
        {
            switch (value)
            {
                case string s:
                    w.Write(s);
                    return;
                case Array array:
                {
                    if (type.GetArrayRank() != 1)
                        throw Unsupported(type, "arrays of more than one dimension");
                    var element = type.GetElementType()!;
                    w.Write(array.Length);
                    if (IsBlittable(element))
                        Method(nameof(WriteBlitArray), element).Invoke(null, [w, array]);
                    else
                        foreach (var item in array)
                            WriteValue(element, item);
                    return;
                }
                case Delegate:
                    throw Unsupported(type, "a delegate");
                case Stream or Thread or Task or Type or MemberInfo:
                    throw Unsupported(type, "holds the world outside");
            }

            if (type == typeof(object) || type == typeof(Lock))
                return;

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var args = type.GetGenericArguments();
                if (definition == typeof(List<>))
                {
                    var list = (IList)value;
                    w.Write(list.Count);
                    if (IsBlittable(args[0]))
                        Method(nameof(WriteBlitList), args[0]).Invoke(null, [w, value]);
                    else
                        foreach (var item in list)
                            WriteValue(args[0], item);
                    return;
                }

                if (definition == typeof(Dictionary<,>) || definition == typeof(ConcurrentDictionary<,>))
                {
                    if (!IsDefaultComparer(type.GetProperty("Comparer")!.GetValue(value), args[0]))
                        throw Unsupported(type, "a comparer of its own");
                    var dictionary = (IDictionary)value;
                    // (A concurrent one's taken as it is now: nothing's adding to it while it's written.)
                    var entries = new List<DictionaryEntry>(dictionary.Count);
                    foreach (DictionaryEntry e in dictionary)
                        entries.Add(e);
                    w.Write(entries.Count);
                    foreach (var e in entries)
                    {
                        WriteValue(args[0], e.Key);
                        WriteValue(args[1], e.Value);
                    }

                    return;
                }

                if (definition == typeof(HashSet<>) || definition == typeof(SortedSet<>))
                {
                    if (definition == typeof(HashSet<>) && !IsDefaultComparer(type.GetProperty("Comparer")!.GetValue(value), args[0]))
                        throw Unsupported(type, "a comparer of its own");
                    var items = ((IEnumerable)value).Cast<object>().ToList();
                    w.Write(items.Count);
                    foreach (var item in items)
                        WriteValue(args[0], item);
                    return;
                }

                if (definition == typeof(Lazy<>))
                    throw Unsupported(type, "a value worked out later");
                if (type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true)
                    throw Unsupported(type, "a collection it doesn't know");
            }

            if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && type.Assembly != typeof(GraphSnapshot).Assembly)
                throw Unsupported(type, "a system type it doesn't know");

            foreach (var f in FieldsOf(type))
                WriteField(f, value);
        }
    }

    private static bool WritePrimitive(BinaryWriter w, Type type, object value)
    {
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean: w.Write(Convert.ToBoolean(value)); return true;
            case TypeCode.Byte: w.Write(Convert.ToByte(value)); return true;
            case TypeCode.SByte: w.Write(Convert.ToSByte(value)); return true;
            case TypeCode.Int16: w.Write(Convert.ToInt16(value)); return true;
            case TypeCode.UInt16: w.Write(Convert.ToUInt16(value)); return true;
            case TypeCode.Int32: w.Write(Convert.ToInt32(value)); return true;
            case TypeCode.UInt32: w.Write(Convert.ToUInt32(value)); return true;
            case TypeCode.Int64: w.Write(Convert.ToInt64(value)); return true;
            case TypeCode.UInt64: w.Write(Convert.ToUInt64(value)); return true;
            case TypeCode.Single: w.Write((float)value); return true;
            case TypeCode.Double: w.Write((double)value); return true;
            case TypeCode.Char: w.Write((ushort)(char)value); return true;
            default: return false;
        }
    }

    private static object? ReadPrimitive(BinaryReader r, Type declared)
    {
        var type = declared.IsEnum ? Enum.GetUnderlyingType(declared) : declared;
        object? v = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => r.ReadBoolean(),
            TypeCode.Byte => r.ReadByte(),
            TypeCode.SByte => r.ReadSByte(),
            TypeCode.Int16 => r.ReadInt16(),
            TypeCode.UInt16 => r.ReadUInt16(),
            TypeCode.Int32 => r.ReadInt32(),
            TypeCode.UInt32 => r.ReadUInt32(),
            TypeCode.Int64 => r.ReadInt64(),
            TypeCode.UInt64 => r.ReadUInt64(),
            TypeCode.Single => r.ReadSingle(),
            TypeCode.Double => r.ReadDouble(),
            TypeCode.Char => (char)r.ReadUInt16(),
            _ => null,
        };
        return v is not null && declared.IsEnum ? Enum.ToObject(declared, v) : v;
    }

    private static void WriteBlit<T>(BinaryWriter w, object value) where T : struct
    {
        var v = (T)value;
        w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref v)));
    }

    private static void WriteBlitArray<T>(BinaryWriter w, Array array) where T : struct => w.Write(MemoryMarshal.AsBytes(((T[])array).AsSpan()));

    private static void WriteBlitList<T>(BinaryWriter w, object list) where T : struct => w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan((List<T>)list)));

    // --- Reading. ---

    /// resolve: an object written as external, found by its (kind, index).
    public sealed class Reader(BinaryReader r, Func<FieldInfo, bool>? skip = null, Func<int, int, object>? resolve = null)
    {
        private readonly List<object> _objects = [];
        private readonly List<Type> _types = [];
        public BinaryReader In => r;

        public object ReadRoot() => ReadRef()!;

        public object? Read(Type declared) => ReadValue(declared);

        /// Fills in an object made already (WriteFields).
        public void ReadInto(object obj)
        {
            foreach (var f in FieldsOf(obj.GetType()))
                ReadField(f, obj);
        }

        private Type ReadType()
        {
            var id = r.ReadInt32();
            if (id >= 0)
                return _types[id];
            var name = r.ReadString();
            var type = Type.GetType(name) ?? throw new InvalidDataException($"Graph snapshot: no type {name}");
            _types.Add(type);
            return type;
        }

        internal object? ReadValue(Type declared)
        {
            if (declared.IsValueType)
            {
                if (Nullable.GetUnderlyingType(declared) is { } inner)
                    return r.ReadBoolean() ? ReadValue(inner) : null;
                if (ReadPrimitive(r, declared) is { } primitive)
                    return primitive;
                if (IsBlittable(declared))
                    return Method(nameof(ReadBlit), declared).Invoke(null, [r]);
                var boxed = RuntimeHelpers.GetUninitializedObject(declared);
                foreach (var f in FieldsOf(declared))
                    ReadField(f, boxed);
                return boxed;
            }

            return ReadRef();
        }

        private void ReadField(FieldInfo f, object owner)
        {
            if (skip?.Invoke(f) == true)
                return;
            f.SetValue(owner, ReadValue(f.FieldType));
        }

        private object? ReadRef()
        {
            switch (r.ReadByte())
            {
                case Null:
                    return null;
                case Seen:
                    return _objects[r.ReadInt32()];
                case Boxed:
                    return ReadValue(ReadType());
                case External:
                {
                    var (kind, index) = (r.ReadInt32(), r.ReadInt32());
                    return (resolve ?? throw new InvalidDataException("Graph snapshot: a reference to elsewhere, with nowhere to find it"))(kind, index);
                }
                case New:
                    break;
                default:
                    throw new InvalidDataException("Graph snapshot: unreadable");
            }

            var type = ReadType();
            if (type == typeof(string))
            {
                var s = r.ReadString();
                _objects.Add(s);
                return s;
            }

            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                var length = r.ReadInt32();
                var array = Array.CreateInstance(element, length);
                _objects.Add(array);
                if (IsBlittable(element))
                    Method(nameof(ReadBlitArray), element).Invoke(null, [r, array]);
                else
                    for (var i = 0; i < length; i++)
                        array.SetValue(ReadValue(element), i);
                return array;
            }

            if (type == typeof(object) || type == typeof(Lock))
            {
                var made = Activator.CreateInstance(type)!;
                _objects.Add(made);
                return made;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var args = type.GetGenericArguments();
                if (definition == typeof(List<>))
                {
                    var count = r.ReadInt32();
                    var list = (IList)Activator.CreateInstance(type, count)!;
                    _objects.Add(list);
                    if (IsBlittable(args[0]))
                        Method(nameof(ReadBlitList), args[0]).Invoke(null, [r, list, count]);
                    else
                        for (var i = 0; i < count; i++)
                            list.Add(ReadValue(args[0]));
                    return list;
                }

                if (definition == typeof(Dictionary<,>) || definition == typeof(ConcurrentDictionary<,>))
                {
                    var dictionary = (IDictionary)Activator.CreateInstance(type)!;
                    _objects.Add(dictionary);
                    for (var n = r.ReadInt32(); n > 0; n--)
                    {
                        var key = ReadValue(args[0])!;
                        dictionary.Add(key, ReadValue(args[1]));
                    }

                    return dictionary;
                }

                if (definition == typeof(HashSet<>) || definition == typeof(SortedSet<>))
                {
                    var set = Activator.CreateInstance(type)!;
                    _objects.Add(set);
                    Method(nameof(ReadSet), args[0]).Invoke(null, [this, set]);
                    return set;
                }
            }

            var obj = RuntimeHelpers.GetUninitializedObject(type);
            _objects.Add(obj);
            foreach (var f in FieldsOf(type))
                ReadField(f, obj);
            return obj;
        }
    }

    private static object ReadBlit<T>(BinaryReader r) where T : struct
    {
        T v = default;
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(new Span<T>(ref v)));
        return v;
    }

    private static void ReadBlitArray<T>(BinaryReader r, Array array) where T : struct => r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(((T[])array).AsSpan()));

    private static void ReadBlitList<T>(BinaryReader r, IList list, int count) where T : struct
    {
        var typed = (List<T>)list;
        CollectionsMarshal.SetCount(typed, count);
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(typed)));
    }

    private static void ReadSet<T>(Reader reader, object set)
    {
        var add = set is HashSet<T> hash ? (Func<T, bool>)hash.Add : ((SortedSet<T>)set).Add;
        for (var n = reader.In.ReadInt32(); n > 0; n--)
            add((T)reader.ReadValue(typeof(T))!);
    }
}
