namespace NetRun.Core;

/// <summary>
/// Distance between two hosts, in network hops, as the transport sees it. The domain does not compute routes:
/// it asks the transport and enforces the reach the action declares (PRIM-003, §8.2).
/// </summary>
public interface IHopDistance
{
    /// <summary>
    /// Hops from one host to another. Zero means the same host; a negative value means the transport cannot
    /// reach it, which is a refusal and not a "probably fine".
    /// </summary>
    int Distance(HostId from, HostId to);
}
