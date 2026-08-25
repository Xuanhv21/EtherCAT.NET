namespace EtherCAT.NET.Engine.Esi;

/// <summary>
/// The outcome of attempting to parse a single ESI XML file found by <see cref="EsiCatalog.LoadFolder"/>.
/// Exactly one of <see cref="Library"/>/<see cref="Error"/> is non-null: a file that failed to
/// parse (malformed XML, not an ESI document, etc.) never aborts the whole folder scan -- it is
/// recorded here instead, alongside every file that did parse successfully.
/// </summary>
/// <param name="FilePath">Full path of the ESI XML file this entry describes.</param>
/// <param name="Library">The parsed library, or <c>null</c> if parsing failed.</param>
/// <param name="Error">A clear description of why parsing failed, or <c>null</c> on success.</param>
public sealed record EsiCatalogEntry(string FilePath, EsiDeviceLibrary? Library, string? Error);

/// <summary>
/// A folder of ESI XML files, each parsed independently. Backs the "drop ESI files from any
/// vendor into one folder" workflow: <see cref="LoadFolder"/> scans a folder for <c>*.xml</c>
/// files and parses every one of them, so the set of vendors/devices the app can discover grows by
/// adding a file, never by a code change.
/// </summary>
/// <param name="Entries">One entry per <c>*.xml</c> file found directly inside the folder (success or failure).</param>
public sealed record EsiCatalog(IReadOnlyList<EsiCatalogEntry> Entries)
{
    /// <summary>Every successfully-parsed library among <see cref="Entries"/>, in the same order.</summary>
    public IReadOnlyList<EsiDeviceLibrary> Libraries { get; } =
        Entries.Where(e => e.Library is not null).Select(e => e.Library!).ToList();

    /// <summary>
    /// Scans <paramref name="folderPath"/> for <c>*.xml</c> files directly inside it (top-level
    /// only, not recursive) and parses each one via <see cref="EsiXmlParser.ParseFile"/>. The
    /// folder is created if it does not already exist, so a fresh install with no ESI files yet is
    /// not an error -- it simply yields an empty <see cref="EsiCatalog"/>. A single malformed or
    /// non-ESI XML file never aborts the scan: its exception is caught and recorded as that file's
    /// <see cref="EsiCatalogEntry.Error"/>, and every other file is still parsed.
    /// </summary>
    public static EsiCatalog LoadFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        Directory.CreateDirectory(folderPath);

        var entries = Directory.EnumerateFiles(folderPath, "*.xml", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(ParseOne)
            .ToList();

        return new EsiCatalog(entries);
    }

    private static EsiCatalogEntry ParseOne(string filePath)
    {
        try
        {
            var library = EsiXmlParser.ParseFile(filePath);
            return new EsiCatalogEntry(filePath, library, Error: null);
        }
        catch (Exception ex)
        {
            return new EsiCatalogEntry(
                filePath,
                Library: null,
                Error: $"Failed to parse '{Path.GetFileName(filePath)}' as an ESI XML file ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>
    /// Ensures <paramref name="folderPath"/> exists and, only if it currently contains zero
    /// <c>*.xml</c> files, writes <paramref name="seedContent"/> out as <paramref name="seedFileName"/>
    /// inside it. Lets a fresh install start with at least one usable ESI file instead of an empty,
    /// useless folder, without ever overwriting or duplicating files a user has already dropped in.
    /// Deliberately generic: the caller supplies the folder, file name, and content, so no
    /// vendor-specific or absolute-path assumption lives in this method.
    /// </summary>
    /// <param name="folderPath">Folder to seed. Created if it does not exist.</param>
    /// <param name="seedFileName">File name to write the seed content as, inside <paramref name="folderPath"/>.</param>
    /// <param name="seedContent">Stream containing the seed file's raw bytes. Read fully and copied as-is.</param>
    public static void SeedIfEmpty(string folderPath, string seedFileName, Stream seedContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(seedFileName);
        ArgumentNullException.ThrowIfNull(seedContent);

        Directory.CreateDirectory(folderPath);

        var alreadyHasXmlFiles = Directory.EnumerateFiles(folderPath, "*.xml", SearchOption.TopDirectoryOnly).Any();
        if (alreadyHasXmlFiles)
        {
            return;
        }

        var destinationPath = Path.Combine(folderPath, seedFileName);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        seedContent.CopyTo(destination);
    }
}
