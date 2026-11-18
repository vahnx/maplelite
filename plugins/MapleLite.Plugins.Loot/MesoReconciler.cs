namespace MapleLite.Plugins.Loot;

/// <summary>
/// Catches meso pickups the notices didn't show. The game shows at most six pickup notices at once; picking up more
/// while six identical ones are up changes nothing on screen, but the meso total in the inventory still goes up.
/// So once the total has settled, the rise since the last settled total is compared with the meso pickups the notices
/// accounted for, and any shortfall is a pickup the notices missed.
/// <para>Only rises that came with at least one notice count: the total also rises from selling to a shop, trades
/// and storage, which show no pickup notice. Falls (dropping coins, buying) just move the baseline.</para>
/// </summary>
internal sealed class MesoReconciler
{
    /// <summary>How long the total must stay unchanged before it's compared: longer than a notice takes to be read.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2.5);

    readonly object _gate = new();
    long? _settled;          // the total the last comparison ended on
    long _noticed;           // mesos counted from notices since then
    int _notices;
    long? _current;          // the latest total read, and since when it has read the same
    DateTimeOffset _since;
    long? _low;              // the lowest total read twice running since the last comparison

    public long? Total { get { lock (_gate) return _current ?? _settled; } }

    /// <summary>A meso pickup counted from a notice.</summary>
    public void Noticed(long amount)
    {
        lock (_gate) { _noticed += amount; _notices++; }
    }

    /// <summary>The total can't be seen (inventory closed or moved): start again from the next total read.</summary>
    public void Lost()
    {
        lock (_gate) { _settled = null; _current = null; _low = null; _noticed = 0; _notices = 0; }
    }

    /// <summary>A total read at <paramref name="now"/>. Returns mesos picked up that no notice showed (0 when none).</summary>
    public long Read(long total, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_settled == null) { _settled = total; _current = total; _since = now; _low = null; _noticed = 0; _notices = 0; return 0; }
            if (total != _current) { _current = total; _since = now; return 0; }
            // Read the same twice: real, not a misread. Coins dropped just before picking things up lower the
            // total briefly; the rise counts from that low point, or the drop would cancel out pickups.
            if (total < (_low ?? long.MaxValue)) _low = total;
            if (now - _since < Settle) return 0;
            long extra = 0;
            if (total != _settled)
            {
                long rise = total - Math.Min(_settled.Value, _low ?? _settled.Value);
                if (rise > 0 && _notices > 0 && rise > _noticed) extra = rise - _noticed;
                _settled = total;
            }
            _noticed = 0; _notices = 0; _low = null;
            return extra;
        }
    }
}
