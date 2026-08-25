using System.Text;
using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.Tests.Esi;

/// <summary>
/// <see cref="EsiCatalog"/> exercised against real temp folders on disk: a folder holding the real
/// embedded Panasonic ESI file plus a hand-written synthetic ESI file for a different fake vendor,
/// side by side with a genuinely broken/non-ESI file -- proving one bad file never aborts the scan
/// -- plus the empty-folder and <see cref="EsiCatalog.SeedIfEmpty"/> seeding behavior.
/// </summary>
public class EsiCatalogTests
{
    public const uint SyntheticVendorId = 0x00001234;
    public const uint SyntheticProductCode = 0x00005678;
    public const uint SyntheticRevisionNumber = 0x00000001;
    public const string SyntheticDeviceName = "FakeSyntheticDevice";

    public static readonly string SyntheticEsiXml = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <EtherCATInfo>
          <Vendor>
            <Id>#x{0x1234:X4}</Id>
            <Name LcId="1033">Fake Synthetic Vendor</Name>
          </Vendor>
          <Descriptions>
            <Devices>
              <Device>
                <Type ProductCode="#x{0x5678:X8}" RevisionNo="#x{0x00000001:X8}">{SyntheticDeviceName}</Type>
              </Device>
            </Devices>
          </Descriptions>
        </EtherCATInfo>
        """;

    private const string BrokenXmlContent = "This is not XML at all -- < unbalanced & malformed >>";

    /// <summary>Copies the real embedded Panasonic ESI resource's raw bytes to <paramref name="destinationPath"/>.</summary>
    private static void WriteEmbeddedPanasonicFile(string destinationPath)
    {
        var assembly = typeof(EsiXmlParser).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(EsiXmlParser.PanasonicMinasA6BeResourceFileName, StringComparison.Ordinal));

        using var resourceStream = assembly.GetManifestResourceStream(resourceName)!;
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        resourceStream.CopyTo(destination);
    }

    private sealed class TempFolder : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("EsiCatalogTests-");

        public string Path(string fileName) => System.IO.Path.Combine(Directory.FullName, fileName);

        public void Dispose()
        {
            try
            {
                Directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; leaking a temp dir must never fail the test.
            }
        }
    }

    [Fact]
    public void LoadFolder_on_a_fresh_empty_folder_returns_zero_entries_without_throwing()
    {
        using var temp = new TempFolder();

        var catalog = EsiCatalog.LoadFolder(temp.Directory.FullName);

        Assert.Empty(catalog.Entries);
        Assert.Empty(catalog.Libraries);
    }

    [Fact]
    public void LoadFolder_creates_the_folder_if_it_does_not_exist_yet()
    {
        using var temp = new TempFolder();
        var notYetCreated = temp.Path("does-not-exist-yet");

        var catalog = EsiCatalog.LoadFolder(notYetCreated);

        Assert.True(System.IO.Directory.Exists(notYetCreated));
        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void LoadFolder_parses_every_xml_file_and_isolates_the_one_broken_file_as_an_error_entry()
    {
        using var temp = new TempFolder();
        WriteEmbeddedPanasonicFile(temp.Path("panasonic.xml"));
        File.WriteAllText(temp.Path("synthetic.xml"), SyntheticEsiXml, Encoding.UTF8);
        File.WriteAllText(temp.Path("broken.xml"), BrokenXmlContent, Encoding.UTF8);

        var catalog = EsiCatalog.LoadFolder(temp.Directory.FullName);

        Assert.Equal(3, catalog.Entries.Count);

        var successes = catalog.Entries.Where(e => e.Library is not null).ToList();
        var failures = catalog.Entries.Where(e => e.Error is not null).ToList();

        Assert.Equal(2, successes.Count);
        Assert.Single(failures);

        var brokenEntry = Assert.Single(catalog.Entries, e => e.FilePath.EndsWith("broken.xml", StringComparison.Ordinal));
        Assert.Null(brokenEntry.Library);
        Assert.False(string.IsNullOrWhiteSpace(brokenEntry.Error));

        Assert.Equal(2, catalog.Libraries.Count);
        Assert.Contains(catalog.Libraries, l => l.Vendor.Id == 0x066Fu);
        Assert.Contains(catalog.Libraries, l => l.Vendor.Id == SyntheticVendorId);
    }

    [Fact]
    public void SeedIfEmpty_writes_the_seed_file_into_a_freshly_created_folder()
    {
        using var temp = new TempFolder();
        var folder = temp.Path("fresh-install-folder");

        using (var seedContent = new MemoryStream(Encoding.UTF8.GetBytes(SyntheticEsiXml)))
        {
            EsiCatalog.SeedIfEmpty(folder, "seed.xml", seedContent);
        }

        var seededPath = Path.Combine(folder, "seed.xml");
        Assert.True(File.Exists(seededPath));

        var library = EsiXmlParser.ParseFile(seededPath);
        Assert.Equal(SyntheticVendorId, library.Vendor.Id);
    }

    [Fact]
    public void SeedIfEmpty_does_not_overwrite_or_duplicate_when_the_folder_already_has_xml_files()
    {
        using var temp = new TempFolder();
        WriteEmbeddedPanasonicFile(temp.Path("panasonic.xml"));

        using (var seedContent = new MemoryStream(Encoding.UTF8.GetBytes(SyntheticEsiXml)))
        {
            EsiCatalog.SeedIfEmpty(temp.Directory.FullName, "seed.xml", seedContent);
        }

        // The folder already had an .xml file, so SeedIfEmpty must be a no-op: no seed.xml written.
        Assert.False(File.Exists(temp.Path("seed.xml")));

        var catalog = EsiCatalog.LoadFolder(temp.Directory.FullName);
        Assert.Single(catalog.Entries);
    }
}
