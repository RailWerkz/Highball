using Highball.Trains;

namespace Highball.Builder;

/// The stock on the track kept with the route: every cut written down as it stands, and put back as it was left when the
/// route's opened again (standing, brakes on, its engines running).
public sealed partial class TrainPanel
{
    /// Every cut as it stands now, its front placed on the map by toGeo (plan x, z to latitude, longitude).
    public List<PlacedCut> Snapshot(Func<double, double, (double Lat, double Lon)> toGeo) => Railway.Snapshot(toGeo);

    /// Puts saved cuts back on the track as they were left (see Railway.Restore). Returns how many went back.
    public int Restore(IReadOnlyList<PlacedCut> saved, Func<double, double, (double X, double Z)> toPlan)
    {
        var put = Railway.Restore(saved, toPlan);
        if (_focus is null && (_controlled ?? _cuts.LastOrDefault()) is { } focus)
            Focus(focus);
        return put;
    }
}
