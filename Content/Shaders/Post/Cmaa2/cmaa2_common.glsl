///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// (Copied from Kitten Space Agency's Vulkan port, unchanged; Intel's licence is LICENSE.txt beside it. Used by Highball/World/Cmaa2.cs.)
// Copyright (c) 2018, Intel Corporation
//
// Licensed under the Apache License, Version 2.0 ( the "License" );
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//
// Conservative Morphological Anti-Aliasing, version: 2.3
//
// Author(s):       Filip Strugar (filip.strugar@intel.com)
//
// More info:       https://github.com/GameTechDev/CMAA2
//
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

// Vulkan GLSL port of Intel's CMAA2 reference implementation.

// The algorithm and packed data layouts remain compatible with the reference. This port makes the
// following engine and Vulkan specific changes.

// 1. Only single sample input is supported. MSAA paths are removed and sample index fields stay zero.

// 2. CMAA2 runs after tonemapping on display encoded LDR color. Intel's version uses sRGB views to
// decode colors for blending. This port uses the active tonemap gamma for an exact decode and encode.
// Edge detection reads the display encoded values directly because its thresholds expect perceptual
// color values.

// 3. Deferred colors use R11G11B10_E4 packing. CMAA2 only changes high contrast edges, so the reduced
// precision is not visible in normal use.

// 4. Half precision paths are not included. The reference disables them by default and documents
// hardware issues in that path.

// 5. Every image and buffer access is checked. HLSL returns zero for invalid reads and ignores invalid
// writes. Vulkan does not guarantee that behavior without robustness features.

// 6. The output is always a typed R8G8B8A8_UNORM storage image. Untyped output paths are not needed.

// The retained options match the reference defaults used in KSA. Quality and luma mode can still be
// changed at compile time.

// Each shader defines one CMAA2_KERNEL macro before including this file. Resource declarations must
// remain conditional so every SPIR V module matches its C# descriptor layout.

// Configuration

// Mode 0 uses full color. Mode 1 uses luma and matches the reference default. Modes 2 and 3
// require precomputed luma and are not included.
#ifndef CMAA2_EDGE_DETECTION_LUMA_PATH
#define CMAA2_EDGE_DETECTION_LUMA_PATH 1
#endif

#define CMAA2_CS_INPUT_KERNEL_SIZE_X                16
#define CMAA2_CS_INPUT_KERNEL_SIZE_Y                16
#define CMAA2_CS_OUTPUT_KERNEL_SIZE_X               (CMAA2_CS_INPUT_KERNEL_SIZE_X-2)
#define CMAA2_CS_OUTPUT_KERNEL_SIZE_Y               (CMAA2_CS_INPUT_KERNEL_SIZE_Y-2)
#define CMAA2_PROCESS_CANDIDATES_NUM_THREADS        128
#define CMAA2_DEFERRED_APPLY_NUM_THREADS            32

// Quality settings

// Maximum Z line search distance. It must be even and cannot exceed 128. Lower values are faster.
const uint c_maxLineLength = 86;

#ifndef CMAA2_EXTRA_SHARPNESS
    #define CMAA2_EXTRA_SHARPNESS                   0     // Preserve more detail but reduce smoothing.
#endif

#ifndef CMAA2_STATIC_QUALITY_PRESET
    #define CMAA2_STATIC_QUALITY_PRESET 2  // 0 low, 1 medium, 2 high, 3 ultra.
#endif
// Edge threshold presets.
#if CMAA2_STATIC_QUALITY_PRESET == 0   // LOW
    #define g_CMAA2_EdgeThreshold                   0.15
#elif CMAA2_STATIC_QUALITY_PRESET == 1 // MEDIUM
    #define g_CMAA2_EdgeThreshold                   0.10
#elif CMAA2_STATIC_QUALITY_PRESET == 2 // HIGH (default)
    #define g_CMAA2_EdgeThreshold                   0.07
#elif CMAA2_STATIC_QUALITY_PRESET == 3 // ULTRA
    #define g_CMAA2_EdgeThreshold                   0.05
#else
    #error CMAA2_STATIC_QUALITY_PRESET not set?
#endif

#if CMAA2_EXTRA_SHARPNESS
#define g_CMAA2_LocalContrastAdaptationAmount       0.15
#define g_CMAA2_SimpleShapeBlurinessAmount          0.07
#else
#define g_CMAA2_LocalContrastAdaptationAmount       0.10
#define g_CMAA2_SimpleShapeBlurinessAmount          0.10
#endif


// Push constants replace GetDimensions and provide buffer limits and display gamma.
layout (push_constant) uniform Cmaa2PushConsts
{
    uint g_screenWidth;
    uint g_screenHeight;
    uint g_candidateCapacity;       // Shape candidate capacity.
    uint g_blendLocationCapacity;   // Blend location capacity.
    uint g_blendItemCapacity;       // Blend item capacity.
    float g_gamma;                  // Display gamma used for decoding.
    float g_invGamma;               // Display gamma used for encoding.
};

// Resources. Bindings match Cmaa2Renderer.cs and are declared only where used.

#if defined(CMAA2_KERNEL_EDGES_COLOR) || defined(CMAA2_KERNEL_PROCESS_CANDIDATES)
// Display encoded LDR input. texelFetch does not use sampler filtering.
layout (set = 0, binding = 0) uniform sampler2D g_inoutColorReadonly;
#endif

#if defined(CMAA2_KERNEL_DEFERRED_COLOR_APPLY) || defined(CMAA2_KERNEL_DEBUG_DRAW_EDGES)
// Final output image. A barrier separates read passes from write passes.
// CMAA2_HDR_COLOR is defined while HDR is on, so highlights above 1 survive here and in the blend packing.
#if defined(CMAA2_HDR_COLOR)
layout (set = 0, binding = 1, rgba16f) uniform writeonly image2D g_inoutColorWriteonly;
#else
layout (set = 0, binding = 1, rgba8) uniform writeonly image2D g_inoutColorWriteonly;
#endif
#endif

#if defined(CMAA2_KERNEL_EDGES_COLOR) || defined(CMAA2_KERNEL_PROCESS_CANDIDATES) || defined(CMAA2_KERNEL_DEBUG_DRAW_EDGES)
// Packed edges. One texel stores two 4 bit masks.
layout (set = 0, binding = 2, r8ui) uniform uimage2D g_workingEdges;
#endif

#if defined(CMAA2_KERNEL_EDGES_COLOR) || defined(CMAA2_KERNEL_PROCESS_CANDIDATES)
layout (std430, set = 0, binding = 3) buffer ShapeCandidatesSSBO
{
    uint g_workingShapeCandidates[];
};
#endif

#if defined(CMAA2_KERNEL_PROCESS_CANDIDATES) || defined(CMAA2_KERNEL_DEFERRED_COLOR_APPLY)
layout (std430, set = 0, binding = 4) buffer DeferredBlendLocationListSSBO
{
    uint g_workingDeferredBlendLocationList[];
};
layout (std430, set = 0, binding = 5) buffer DeferredBlendItemListSSBO
{
    uvec2 g_workingDeferredBlendItemList[];
};
#endif

#if defined(CMAA2_KERNEL_EDGES_COLOR) || defined(CMAA2_KERNEL_PROCESS_CANDIDATES) || defined(CMAA2_KERNEL_DEFERRED_COLOR_APPLY)
// Linked list head for each 2 by 2 pixel quad.
layout (set = 0, binding = 6, r32ui) uniform uimage2D g_workingDeferredBlendItemListHeads;
#endif

#if defined(CMAA2_KERNEL_EDGES_COLOR) || defined(CMAA2_KERNEL_COMPUTE_DISPATCH_ARGS) || defined(CMAA2_KERNEL_PROCESS_CANDIDATES) || defined(CMAA2_KERNEL_DEFERRED_COLOR_APPLY)
// Sixteen uint values. Counters use elements 4, 8, and 12 to reduce atomic contention.
// Element 3 stores the item count for the next indirect dispatch.
layout (std430, set = 0, binding = 7) buffer ControlBufferSSBO
{
    uint g_workingControlBuffer[];
};
#endif

#if defined(CMAA2_KERNEL_COMPUTE_DISPATCH_ARGS)
// Four uint values. The first three match VkDispatchIndirectCommand.
layout (std430, set = 0, binding = 8) buffer ExecuteIndirectBufferSSBO
{
    uint g_workingExecuteIndirectBuffer[];
};
#endif

// Bounds helpers
ivec2 ScreenSize()
{
    return ivec2(int(g_screenWidth), int(g_screenHeight));
}
bool IsInScreen(ivec2 pixelPos)
{
    return !(any(lessThan(pixelPos, ivec2(0))) || any(greaterThanEqual(pixelPos, ScreenSize())));
}

// Edge encoding
// Edge mask bit layout:

//                    Top 0x08
//              |=============|
//   Left 0x04  |    pixel    |  Right 0x01
//              |=============|
//                  Bottom 0x02
uint PackEdges( vec4 edges )   // Edge values are either zero or one.
{
    return uint( dot( edges, vec4( 1, 2, 4, 8 ) ) );
}
vec4 UnpackEdgesFlt( uint value )
{
    vec4 ret;
    ret.x = float( ( value & 0x01u ) != 0u );
    ret.y = float( ( value & 0x02u ) != 0u );
    ret.z = float( ( value & 0x04u ) != 0u );
    ret.w = float( ( value & 0x08u ) != 0u );
    return ret;
}

// Color helpers

// Intel's reference uses sRGB views for this conversion. The port applies the tonemap gamma
// directly because the display encoding is configurable.
vec3 DisplayToLinear( vec3 color )
{
    return pow( color, vec3( g_gamma ) );
}
vec3 LinearToDisplay( vec3 color )
{
    return pow( color, vec3( g_invGamma ) );
}

#if defined(CMAA2_KERNEL_EDGES_COLOR) || defined(CMAA2_KERNEL_PROCESS_CANDIDATES)
// Edge detection reads display encoded color.
vec3 LoadSourceColor( ivec2 pixelPos, ivec2 offset )
{
    ivec2 pos = pixelPos + offset;
    // Match HLSL by returning zero outside the image.
    if( !IsInScreen( pos ) )
        return vec3( 0.0 );
    return texelFetch( g_inoutColorReadonly, pos, 0 ).rgb;
}
// Blending uses linear color.
vec3 LoadSourceColorLinear( ivec2 pixelPos, ivec2 offset )
{
    return DisplayToLinear( LoadSourceColor( pixelPos, offset ) );
}
// HLSL converts negative Z blend coordinates to zero before loading. Clamp only the negative side
// to match that behavior. Positive coordinates outside the image still return zero.
vec3 LoadBlendColorLinear( ivec2 pixelPos )
{
    return LoadSourceColorLinear( max( pixelPos, ivec2( 0 ) ), ivec2( 0, 0 ) );
}
#endif

// R11G11B10 conversion adapted from MiniEngine PixelPacking_R11G11B10.hlsli.
// Copyright Microsoft. MIT license. Author James Stanard.
// https://github.com/Microsoft/DirectX-Graphics-Samples/blob/master/MiniEngine/Core/Shaders/PixelPacking_R11G11B10.hlsli

// Match the exact half float bit pattern produced by D3D. D3D truncates while packHalf2x16 rounds,
// which can change the packed 11 bit value. Input is already limited to the range from zero to two.
uint f32tof16( float val )
{
    uint bits       = floatBitsToUint( val );
    int exponent    = int( ( bits >> 23 ) & 0xFFu ) - 127;
    uint mantissa   = bits & 0x007FFFFFu;
    if( exponent >= -14 )                                   // normal half
        return ( uint( exponent + 15 ) << 10 ) | ( mantissa >> 13 );
    if( exponent >= -24 )                                   // subnormal half, implicit 1 shifted back in
        return ( 0x00800000u | mantissa ) >> uint( -exponent - 1 );
    return 0u;                                              // underflows to zero
}
float f16tof32( uint val )
{
    return unpackHalf2x16( val & 0xFFFFu ).x;
}

// This format gives each mantissa one extra bit by taking one exponent bit. Its precision is enough
// for the deferred LDR blend colors used here.
uint Pack_R11G11B10_E4_FLOAT( vec3 rgb )
{
    // Clamp below 2.0 so one fewer exponent bit is needed.
    rgb = clamp( rgb, vec3( 0.0 ), vec3( uintBitsToFloat( 0x3FFFFFFFu ) ) );
    uint r = ((f32tof16(rgb.r) + 4u) >> 3 ) & 0x000007FFu;
    uint g = ((f32tof16(rgb.g) + 4u) << 8 ) & 0x003FF800u;
    uint b = ((f32tof16(rgb.b) + 8u) << 18) & 0xFFC00000u;
    return r | g | b;
}

vec3 Unpack_R11G11B10_E4_FLOAT( uint rgb )
{
    float r = f16tof32((rgb << 3 ) & 0x3FF8u);
    float g = f16tof32((rgb >> 8 ) & 0x3FF8u);
    float b = f16tof32((rgb >> 18) & 0x3FF0u);
    return vec3(r, g, b);
}

// Standard R11G11B10 float layout with the full exponent, for HDR colors that run well past 2.0.
uint Pack_R11G11B10_FLOAT( vec3 rgb )
{
    // Largest value all three channels hold without rounding up into infinity
    rgb = clamp( rgb, vec3( 0.0 ), vec3( 64512.0 ) );
    uint r = ((f32tof16(rgb.r) + 8u ) >> 4 ) & 0x000007FFu;
    uint g = ((f32tof16(rgb.g) + 8u ) << 7 ) & 0x003FF800u;
    uint b = ((f32tof16(rgb.b) + 16u) << 17) & 0xFFC00000u;
    return r | g | b;
}

vec3 Unpack_R11G11B10_FLOAT( uint rgb )
{
    float r = f16tof32((rgb << 4 ) & 0x7FF0u);
    float g = f16tof32((rgb >> 7 ) & 0x7FF0u);
    float b = f16tof32((rgb >> 17) & 0x7FE0u);
    return vec3(r, g, b);
}

// Reduced precision is acceptable because CMAA2 changes high contrast edges, not smooth gradients.
vec3 InternalUnpackColor( uint packedColor )
{
#if defined(CMAA2_HDR_COLOR)
    return Unpack_R11G11B10_FLOAT( packedColor );
#else
    return Unpack_R11G11B10_E4_FLOAT( packedColor );
#endif
}

uint InternalPackColor( vec3 color )
{
#if defined(CMAA2_HDR_COLOR)
    return Pack_R11G11B10_FLOAT( color );
#else
    return Pack_R11G11B10_E4_FLOAT( color );
#endif
}


#if defined(CMAA2_KERNEL_PROCESS_CANDIDATES)
void StoreColorSample( ivec2 pixelPos, vec3 color, bool isComplexShape )
{
    // Drop invalid samples and full buffer appends before changing the linked list head.
    if( !IsInScreen( pixelPos ) )
        return;
    uint counterIndex = atomicAdd( g_workingControlBuffer[12], 1u );
    if( counterIndex >= g_blendItemCapacity )
        return;

    // Quad position.
    ivec2 quadPos       = pixelPos / 2;
    // Pixel position inside the quad.
    uint offsetXY       = uint(pixelPos.y % 2) * 2u + uint(pixelPos.x % 2);
    // Header layout: 2 quad offset bits, 3 unused sample bits, 1 complex shape bit,
    // and 26 item index bits.
    uint header         = ( offsetXY << 30 ) | ( uint(isComplexShape) << 26 );

    uint counterIndexWithHeader = counterIndex | header;

    uint originalIndex = imageAtomicExchange( g_workingDeferredBlendItemListHeads, quadPos, counterIndexWithHeader );
    g_workingDeferredBlendItemList[counterIndex] = uvec2( originalIndex, InternalPackColor( color ) );

    // Add this quad to the apply list when the first item is inserted.
    if( originalIndex == 0xFFFFFFFFu )
    {
        // Record the quad so the apply pass can find it.
        uint edgeListCounter = atomicAdd( g_workingControlBuffer[8], 1u );
        if( edgeListCounter < g_blendLocationCapacity )
            g_workingDeferredBlendLocationList[edgeListCounter] = (uint(quadPos.x) << 16) | uint(quadPos.y);
    }
}

// Share Z shape sampling across the whole workgroup.
#define CMAA2_BLEND_ITEM_SLM_SIZE           768         // Direct processing is used when this list is full.
shared uint     g_groupSharedBlendItemCount;
shared uvec2    g_groupSharedBlendItems[ CMAA2_BLEND_ITEM_SLM_SIZE ];
#endif // CMAA2_KERNEL_PROCESS_CANDIDATES

// Final color store

#if defined(CMAA2_KERNEL_DEFERRED_COLOR_APPLY) || defined(CMAA2_KERNEL_DEBUG_DRAW_EDGES)
// Converts linear color back to display gamma and stores it in the color image.
void FinalUAVStore( ivec2 pixelPos, vec3 color )
{
    // Border quads can address one pixel outside the image.
    if( !IsInScreen( pixelPos ) )
        return;
    imageStore( g_inoutColorWriteonly, pixelPos, vec4( LinearToDisplay( color ), 0.0 ) );
}
#endif



// Edge detection and local contrast helpers

float GetActualEdgeThreshold( )
{
    return g_CMAA2_EdgeThreshold;
}

float EdgeDetectColorCalcDiff( vec3 colorA, vec3 colorB )
{
    const vec3 LumWeights = vec3( 0.299, 0.587, 0.114 );
    vec3 diff = abs( colorA.rgb - colorB.rgb );
    return dot( diff.rgb, LumWeights.rgb );
}

// Intel's shader applies sqrt to approximate perceptual color from linear input. This port already
// receives display encoded color, so no conversion is needed.
vec3 ProcessColorForEdgeDetect( vec3 color )
{
    return color;
}

vec2 ComputeEdge( int x, int y, vec3 pixelColors[3 * 3 - 1] )
{
    vec2 temp;
    temp.x = EdgeDetectColorCalcDiff( pixelColors[x + y * 3].rgb, pixelColors[x + 1 + y * 3].rgb );
    temp.y = EdgeDetectColorCalcDiff( pixelColors[x + y * 3].rgb, pixelColors[x + ( y + 1 ) * 3].rgb );
    return temp;
}
// Convert display encoded color to luma for edge detection. The reference applies sqrt first
// because its input is linear. This port already receives gamma encoded color.
float RGBToLumaForEdges( vec3 displayEncodedRGB )
{
    return dot( displayEncodedRGB.rgb, vec3( 0.299, 0.587, 0.114 ) );  // http://en.wikipedia.org/wiki/CCIR_601
}
vec2 ComputeEdgeLuma( int x, int y, float pixelLumas[3 * 3 - 1] )
{
    vec2 temp;
    temp.x = abs( pixelLumas[x + y * 3] - pixelLumas[x + 1 + y * 3] );
    temp.y = abs( pixelLumas[x + y * 3] - pixelLumas[x + ( y + 1 ) * 3] );
    return temp;
}

float ComputeLocalContrastV( int x, int y, vec2 neighbourhood[4][4] )
{
    // Compare the four connected edges.
    return max( max( neighbourhood[x + 1][y + 0].y, neighbourhood[x + 1][y + 1].y ), max( neighbourhood[x + 2][y + 0].y, neighbourhood[x + 2][y + 1].y ) ) * float( g_CMAA2_LocalContrastAdaptationAmount );
}

float ComputeLocalContrastH( int x, int y, vec2 neighbourhood[4][4] )
{
    // Compare the four connected edges.
    return max( max( neighbourhood[x + 0][y + 1].x, neighbourhood[x + 1][y + 1].x ), max( neighbourhood[x + 0][y + 2].x, neighbourhood[x + 1][y + 2].x ) ) * float( g_CMAA2_LocalContrastAdaptationAmount );
}

vec4 ComputeSimpleShapeBlendValues( vec4 edges, vec4 edgesLeft, vec4 edgesRight, vec4 edgesTop, vec4 edgesBottom, bool dontTestShapeValidity )
{
    // Use a 3 by 3 neighborhood for simple corner shapes.

    float fromRight   = edges.r;
    float fromBelow   = edges.g;
    float fromLeft    = edges.b;
    float fromAbove   = edges.a;

    float blurCoeff = float( g_CMAA2_SimpleShapeBlurinessAmount );

    float numberOfEdges = dot( edges, vec4( 1, 1, 1, 1 ) );

    float numberOfEdgesAllAround = dot(edgesLeft.bga + edgesRight.rga + edgesTop.rba + edgesBottom.rgb, vec3( 1, 1, 1 ) );

    // Skip repeated validity checks for confirmed candidates.
    if( !dontTestShapeValidity )
    {
        // Keep straight edges sharp.
        if( numberOfEdges == 1.0 )
            blurCoeff = 0.0;

        // Blur corners but not two parallel edges.
        if( numberOfEdges == 2.0 )
            blurCoeff *= ( ( 1.0 - fromBelow * fromAbove ) * ( 1.0 - fromRight * fromLeft ) );
    }

    // Adjust corner shaped steps.
    if( numberOfEdges == 2.0 )
    {
        blurCoeff *= 0.75;

        float k = 0.9;
        fromRight   += k * (edges.g * edgesTop.r     * (1.0-edgesLeft.g)   +     edges.a * edgesBottom.r   * (1.0-edgesLeft.a)      );
        fromBelow   += k * (edges.b * edgesRight.g   * (1.0-edgesTop.b)    +     edges.r * edgesLeft.g     * (1.0-edgesTop.r)       );
        fromLeft    += k * (edges.a * edgesBottom.b  * (1.0-edgesRight.a)  +     edges.g * edgesTop.b      * (1.0-edgesRight.g)     );
        fromAbove   += k * (edges.r * edgesLeft.a    * (1.0-edgesBottom.r) +     edges.b * edgesRight.a   *  (1.0-edgesBottom.b)    );
    }

    // Reduce blur in detailed areas to preserve text and texture.
#if CMAA2_EXTRA_SHARPNESS
    blurCoeff *= clamp( 1.15 - numberOfEdgesAllAround / 8.0, 0.0, 1.0 );
#else
    blurCoeff *= clamp( 1.30 - numberOfEdgesAllAround / 10.0, 0.0, 1.0 );
#endif

    return vec4( fromLeft, fromAbove, fromRight, fromBelow ) * blurCoeff;
}

#if defined(CMAA2_KERNEL_PROCESS_CANDIDATES) || defined(CMAA2_KERNEL_DEBUG_DRAW_EDGES)
uint LoadEdge( ivec2 pixelPos, ivec2 offset )
{
    ivec2 pos = pixelPos + offset;
    // Z line tracing depends on zero outside the image. Do not clamp edge coordinates because a
    // repeated border value could keep the trace running.
    //
    // At odd widths, Intel's packed texture can expose the unused nibble beside the last pixel. The
    // current tracing masks do not read that bit, so returning zero remains equivalent.
    if( !IsInScreen( pos ) )
        return 0u;
    // Read one of the two 4 bit masks stored in each texel.
    uint a      = uint(pos.x) % 2u;
    uint edge   = imageLoad( g_workingEdges, ivec2( pos.x / 2, pos.y ) ).x;
    edge = (edge >> (a*4u)) & 0xFu;
    return edge;
}
#endif

#if defined(CMAA2_KERNEL_EDGES_COLOR)
shared vec4 g_groupShared2x2FracEdgesH[CMAA2_CS_INPUT_KERNEL_SIZE_X * CMAA2_CS_INPUT_KERNEL_SIZE_Y];
shared vec4 g_groupShared2x2FracEdgesV[CMAA2_CS_INPUT_KERNEL_SIZE_X * CMAA2_CS_INPUT_KERNEL_SIZE_Y];
void GroupsharedLoadQuadHV( uint addr, out vec2 e00, out vec2 e10, out vec2 e01, out vec2 e11 )
{
    vec4 valH = g_groupShared2x2FracEdgesH[addr]; e00.y = valH.x; e10.y = valH.y; e01.y = valH.z; e11.y = valH.w;
    vec4 valV = g_groupShared2x2FracEdgesV[addr]; e00.x = valV.x; e10.x = valV.y; e01.x = valV.z; e11.x = valV.w;
}
#endif

#if defined(CMAA2_KERNEL_PROCESS_CANDIDATES)
// Z shape detection and blending

void FindZLineLengths( out float lineLengthLeft, out float lineLengthRight, ivec2 screenPos, bool horizontal, bool invertedZShape, vec2 stepRight )
{
// This extra rejection test reduces smoothing too much, so it remains disabled.
#define CMAA2_EXTRA_CONSERVATIVENESS2 0
    // TODO: Replace this mask setup with a small lookup table.
    uint maskLeft, bitsContinueLeft, maskRight, bitsContinueRight;
    {
        // Vertical tracing uses the same masks after a 90 degree rotation.
        // Inverted Z case:              // Normal Z case:
        //   __                          // __
        //  X|                           //  X|
        // --                            //   --
        uint maskTraceLeft, maskTraceRight;
        if( horizontal )
        {
            maskTraceLeft = 0x08u; // tracing top edge
            maskTraceRight = 0x02u; // tracing bottom edge
        }
        else
        {
            maskTraceLeft = 0x04u; // tracing left edge
            maskTraceRight = 0x01u; // tracing right edge
        }
        if( invertedZShape )
        {
            uint temp = maskTraceLeft;
            maskTraceLeft = maskTraceRight;
            maskTraceRight = temp;
        }
        maskLeft = maskTraceLeft;
        bitsContinueLeft = maskTraceLeft;
        maskRight = maskTraceRight;
        bitsContinueRight = maskTraceRight;
    }

    bool continueLeft = true;
    bool continueRight = true;
    lineLengthLeft = 1.0;
    lineLengthRight = 1.0;
    for( ; ; )
    {
        // Match the reference by truncating the whole number float positions to integers.
        uint edgeLeft =     LoadEdge( ivec2( vec2(screenPos) - stepRight * lineLengthLeft ),           ivec2( 0, 0 ) );
        uint edgeRight =    LoadEdge( ivec2( vec2(screenPos) + stepRight * ( lineLengthRight + 1.0 ) ), ivec2( 0, 0 ) );

        // Stop when the required edge pattern ends.
        continueLeft    = continueLeft  && ( ( edgeLeft & maskLeft ) == bitsContinueLeft );
        continueRight   = continueRight && ( ( edgeRight & maskRight ) == bitsContinueRight );

        lineLengthLeft += float(continueLeft);
        lineLengthRight += float(continueRight);

        float maxLR = max( lineLengthRight, lineLengthLeft );

        // End immediately when both directions stop.
        if( !continueLeft && !continueRight )
            maxLR = float(c_maxLineLength);

        // End when one side grows too far beyond the other or both sides have stopped.
#if CMAA2_EXTRA_SHARPNESS
        if( maxLR >= min( float(c_maxLineLength), (1.20 * min( lineLengthRight, lineLengthLeft ) - 0.20) ) )
#else
        if( maxLR >= min( float(c_maxLineLength), (1.25 * min( lineLengthRight, lineLengthLeft ) - 0.25) ) )
#endif
            break;
    }
}

// Empirical Z blend settings.
const float c_symmetryCorrectionOffset = 0.22;
#if CMAA2_EXTRA_SHARPNESS
const float c_dampeningEffect          = 0.11;
#else
const float c_dampeningEffect          = 0.15;
#endif

bool CollectBlendZs( ivec2 screenPos, bool horizontal, bool invertedZShape, float shapeQualityScore, float lineLengthLeft, float lineLengthRight, vec2 stepRight )
{
    // Match HLSL remainder behavior when line lengths become negative.
    float leftOdd = c_symmetryCorrectionOffset * ( lineLengthLeft - 2.0 * trunc( lineLengthLeft / 2.0 ) );
    float rightOdd = c_symmetryCorrectionOffset * ( lineLengthRight - 2.0 * trunc( lineLengthRight / 2.0 ) );

    float dampenEffect = clamp( (lineLengthLeft + lineLengthRight - shapeQualityScore) * c_dampeningEffect, 0.0, 1.0 );

    float loopFrom = -floor( ( lineLengthLeft + 1.0 ) / 2.0 ) + 1.0;
    float loopTo = floor( ( lineLengthRight + 1.0 ) / 2.0 );

    uint blendItemCount = uint(loopTo - loopFrom) + 1u;
    uint itemIndex = atomicAdd( g_groupSharedBlendItemCount, blendItemCount );
    // Use direct processing when shared memory is full.
    if( (itemIndex + blendItemCount) > uint(CMAA2_BLEND_ITEM_SLM_SIZE) )
        return false;

    float totalLength = (loopTo - loopFrom) + 1.0 - leftOdd - rightOdd;
    float lerpStep = 1.0 / totalLength;

    float lerpFromK = (0.5 - leftOdd - loopFrom) * lerpStep;

    // Item layout: 14 X bits, 4 unused sample bits, and 14 Y bits.
    uint itemHeader     = (uint(screenPos.x) << 18) | uint(screenPos.y);
    uint itemValStatic  = (uint(horizontal) << 31) | (uint(invertedZShape) << 30);

    for( float i = loopFrom; i <= loopTo; i++ )
    {
        float secondPart = float(i > 0.0);
        float srcOffset = 1.0 - secondPart * 2.0;

        float lerpK = (lerpStep * i + lerpFromK) * srcOffset + secondPart;
        lerpK *= dampenEffect;

        uvec2 encodedItem;
        encodedItem.x = itemHeader;
        encodedItem.y = itemValStatic | ((uint(i+256.0) /*& 0x3FF*/) << 20) | ( (uint(srcOffset+256.0) /*& 0x3FF*/ ) << 10 ) | uint( clamp(lerpK, 0.0, 1.0) * 1023.0 + 0.5 );
        g_groupSharedBlendItems[itemIndex++] = encodedItem;
    }
    return true;
}

// Direct fallback used when the shared blend list is full. Output stays correct but costs more.
void BlendZs( ivec2 screenPos, bool horizontal, bool invertedZShape, float shapeQualityScore, float lineLengthLeft, float lineLengthRight, vec2 stepRight )
{
    vec2 blendDir = ( horizontal ) ? ( vec2( 0, -1 ) ) : ( vec2( -1, 0 ) );

    if( invertedZShape )
        blendDir = -blendDir;

    // Match HLSL remainder behavior when line lengths become negative.
    float leftOdd = c_symmetryCorrectionOffset * ( lineLengthLeft - 2.0 * trunc( lineLengthLeft / 2.0 ) );
    float rightOdd = c_symmetryCorrectionOffset * ( lineLengthRight - 2.0 * trunc( lineLengthRight / 2.0 ) );

    float dampenEffect = clamp( (lineLengthLeft + lineLengthRight - shapeQualityScore) * c_dampeningEffect, 0.0, 1.0 );

    float loopFrom = -floor( ( lineLengthLeft + 1.0 ) / 2.0 ) + 1.0;
    float loopTo = floor( ( lineLengthRight + 1.0 ) / 2.0 );

    float totalLength = (loopTo - loopFrom) + 1.0 - leftOdd - rightOdd;
    float lerpStep = 1.0 / totalLength;

    float lerpFromK = (0.5 - leftOdd - loopFrom) * lerpStep;

    for( float i = loopFrom; i <= loopTo; i++ )
    {
        float secondPart = float(i > 0.0);
        float srcOffset = 1.0 - secondPart * 2.0;

        float lerpK = (lerpStep * i + lerpFromK) * srcOffset + secondPart;
        lerpK *= dampenEffect;

        ivec2 pixelPos = ivec2( vec2(screenPos) + stepRight * i );

        // Blend in linear color to match the reference.
        vec3 colorCenter    = LoadBlendColorLinear( pixelPos );
        vec3 colorFrom      = LoadBlendColorLinear( pixelPos + ivec2( blendDir * srcOffset ) );

        vec3 outputColor = mix( colorCenter.rgb, colorFrom.rgb, lerpK );

        StoreColorSample( pixelPos, outputColor, true );
    }
}

// Intel reported hardware issues with the half precision version of this code. Keep this path in
// full precision until that behavior is understood.
void DetectZsHorizontal( in vec4 edges, in vec4 edgesM1P0, in vec4 edgesP1P0, in vec4 edgesP2P0, out float invertedZScore, out float normalZScore )
{
    // Inverted Z case:
    //   __
    //  X|
    // --
    {
        invertedZScore  = edges.r * edges.g *                edgesP1P0.a;
        invertedZScore  *= 2.0 + ((edgesM1P0.g + edgesP2P0.a) ) - (edges.a + edgesP1P0.g) - 0.7 * (edgesP2P0.g + edgesM1P0.a + edges.b + edgesP1P0.r);
    }

    // Normal Z case:
    // __
    //  X|
    //   --
    {
        normalZScore    = edges.r * edges.a *                edgesP1P0.g;
        normalZScore    *= 2.0 + ((edgesM1P0.a + edgesP2P0.g) ) - (edges.g + edgesP1P0.a) - 0.7 * (edgesP2P0.a + edgesM1P0.g + edges.b + edgesP1P0.r);
    }
}
#endif // CMAA2_KERNEL_PROCESS_CANDIDATES
