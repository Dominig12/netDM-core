namespace NetDM.Services.Research;

/// <summary>
/// Damage that an interrupted append cannot explain: a whole record header declares an impossible
/// length, or a whole record fails its checksum. Everything before the damage was flushed before it was
/// acknowledged, so shortening the file would silently drop confirmed research records. The journal is
/// therefore left byte for byte as it was and research writes stay blocked until an operator restores it.
/// A torn tail — fewer bytes than the record needs — is not damage: it is cut off by
/// <see cref="ResearchJournal.Repair"/> after replay, as before.
/// </summary>
public sealed class ResearchJournalCorruptException(string path, string reason, long offset, int wholeRecords, long length)
    : InvalidOperationException(
        $"'{path}' is damaged at byte {offset} after {wholeRecords} whole record(s): {reason}. " +
        "The journal is not repaired or shortened; research writes stay blocked until it is restored.")
{
    public string Path { get; } = path;
    /// <summary><c>checksum</c> or <c>invalid_length</c>.</summary>
    public string Reason { get; } = reason;
    /// <summary>Byte offset of the first damaged record, right after the last whole one.</summary>
    public long Offset { get; } = offset;
    /// <summary>Whole records replayed before the damage.</summary>
    public int WholeRecords { get; } = wholeRecords;
    /// <summary>Length of the journal at the time it was read.</summary>
    public long Length { get; } = length;
}
