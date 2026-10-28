using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

/// <summary>
/// Reads package metadata without launching or installing the package.
/// </summary>
public sealed class PackageMetadataReader
{
    private const int MaxZipEntriesToInspect = 10_000;
    private const int MaxArchiveDirectoryBytes = 16 * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxXmlNodes = 50_000;
    private const int MaxXmlDepth = 128;
    private const int MaxZipManifestsPerType = 16;

    public PackageMetadata Read(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        try
        {
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return ReadExecutable(filePath);

            if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                return MsiMetadataReader.Read(filePath);

            if (extension.Equals(".msix", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".appx", StringComparison.OrdinalIgnoreCase))
                return ReadAppPackage(filePath);

            if (extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase))
                return ReadBundle(filePath);

            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                return ReadZipPackage(filePath);

            return new PackageMetadata { Warning = "La lectura de metadatos internos para este formato está pendiente." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or XmlException or InvalidOperationException or JsonException)
        {
            return new PackageMetadata { Warning = "No se pudieron leer los metadatos internos del paquete.", IsCacheable = false };
        }
    }

    private static PackageMetadata ReadExecutable(string filePath)
    {
        var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
        return new PackageMetadata
        {
            ProductName = versionInfo.ProductName?.Trim() ?? string.Empty,
            ProductVersion = versionInfo.ProductVersion?.Trim() ?? string.Empty,
            Publisher = versionInfo.CompanyName?.Trim() ?? string.Empty
        };
    }

    private static PackageMetadata ReadAppPackage(string filePath)
    {
        using var archive = OpenBoundedArchive(filePath);
        var manifest = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("AppxManifest.xml not found.");
        var manifestBytes = ReadBoundedEntry(manifest)
            ?? throw new InvalidDataException("AppxManifest.xml exceeds the metadata size limit.");
        using var stream = new MemoryStream(manifestBytes, writable: false);
        var document = LoadSafeXml(stream);
        var identity = FindElement(document.Root, "Identity");
        if (identity is null)
            throw new InvalidDataException("Package Identity not found.");

        var properties = FindElement(document.Root, "Properties");
        var name = Attribute(identity, "Name");
        var displayName = ElementValue(properties, "DisplayName");
        var publisherDisplayName = ElementValue(properties, "PublisherDisplayName");
        return new PackageMetadata
        {
            ProductName = IsLocalizedResource(displayName) ? name : FirstNonEmpty(displayName, name),
            ProductVersion = Attribute(identity, "Version"),
            Publisher = FirstNonEmpty(publisherDisplayName, Attribute(identity, "Publisher")),
            Architecture = Attribute(identity, "ProcessorArchitecture"),
            PackageIdentity = name
        };
    }

    private static PackageMetadata ReadBundle(string filePath)
    {
        using var archive = OpenBoundedArchive(filePath);
        var manifest = archive.GetEntry("AppxMetadata/AppxBundleManifest.xml")
            ?? throw new InvalidDataException("AppxBundleManifest.xml not found.");
        var manifestBytes = ReadBoundedEntry(manifest)
            ?? throw new InvalidDataException("AppxBundleManifest.xml exceeds the metadata size limit.");
        using var stream = new MemoryStream(manifestBytes, writable: false);
        var document = LoadSafeXml(stream);
        var identity = FindElement(document.Root, "Identity");
        if (identity is null)
            throw new InvalidDataException("Bundle Identity not found.");

        var name = Attribute(identity, "Name");
        var architectures = document.Descendants()
            .Where(element => element.Name.LocalName == "Package")
            .Select(element => Attribute(element, "Architecture"))
            .Where(architecture => !string.IsNullOrWhiteSpace(architecture))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return new PackageMetadata
        {
            ProductName = name,
            ProductVersion = Attribute(identity, "Version"),
            Publisher = Attribute(identity, "Publisher"),
            Architecture = string.Join(", ", architectures),
            PackageIdentity = name
        };
    }

    private static PackageMetadata ReadZipPackage(string filePath)
    {
        using var archive = OpenBoundedArchive(filePath);
        var nuspecEntries = new List<ZipArchiveEntry>();
        var packageJsonEntries = new List<ZipArchiveEntry>();
        var inspectedEntries = 0;

        foreach (var entry in archive.Entries)
        {
            if (++inspectedEntries > MaxZipEntriesToInspect)
                return new PackageMetadata { Warning = "El ZIP contiene demasiados elementos para buscar metadatos de forma segura." };

            if (string.IsNullOrEmpty(entry.Name))
                continue;

            if (Path.GetExtension(entry.Name).Equals(".nuspec", StringComparison.OrdinalIgnoreCase)
                && nuspecEntries.Count < MaxZipManifestsPerType)
                nuspecEntries.Add(entry);
            else if (entry.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                && packageJsonEntries.Count < MaxZipManifestsPerType)
                packageJsonEntries.Add(entry);
        }

        foreach (var entry in nuspecEntries)
        {
            var metadata = TryReadNuspec(entry);
            if (metadata is not null)
                return metadata;
        }

        foreach (var entry in packageJsonEntries)
        {
            var metadata = TryReadPackageJson(entry);
            if (metadata is not null)
                return metadata;
        }

        return new PackageMetadata
        {
            Warning = nuspecEntries.Count == 0 && packageJsonEntries.Count == 0
                ? "El ZIP no contiene un manifiesto compatible (.nuspec o package.json)."
                : "No se pudieron obtener metadatos válidos de los manifiestos del ZIP."
        };
    }

    private static ZipArchive OpenBoundedArchive(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            EnsureBoundedArchiveDirectory(stream);
            stream.Position = 0;
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureBoundedArchiveDirectory(FileStream stream)
    {
        const uint endOfCentralDirectorySignature = 0x06054B50;
        const uint zip64LocatorSignature = 0x07064B50;
        const uint zip64EndOfCentralDirectorySignature = 0x06064B50;
        const int endOfCentralDirectoryMinimumLength = 22;
        const int maximumZipCommentLength = ushort.MaxValue;

        if (stream.Length < endOfCentralDirectoryMinimumLength)
            throw new InvalidDataException("The archive is too short to contain a directory record.");

        var tailLength = (int)Math.Min(stream.Length, endOfCentralDirectoryMinimumLength + maximumZipCommentLength);
        var tailOffset = stream.Length - tailLength;
        stream.Position = tailOffset;
        var tail = new byte[tailLength];
        stream.ReadExactly(tail);

        var endRecordIndex = -1;
        for (var index = tail.Length - endOfCentralDirectoryMinimumLength; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, sizeof(uint))) != endOfCentralDirectorySignature)
                continue;

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, sizeof(ushort)));
            if (index + endOfCentralDirectoryMinimumLength + commentLength == tail.Length)
            {
                endRecordIndex = index;
                break;
            }
        }

        if (endRecordIndex < 0)
            throw new InvalidDataException("The archive directory record could not be validated.");

        var endRecord = tail.AsSpan(endRecordIndex);
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord.Slice(4, sizeof(ushort)));
        var directoryDiskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord.Slice(6, sizeof(ushort)));
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord.Slice(8, sizeof(ushort)));
        var totalEntries16 = BinaryPrimitives.ReadUInt16LittleEndian(endRecord.Slice(10, sizeof(ushort)));
        var directorySize32 = BinaryPrimitives.ReadUInt32LittleEndian(endRecord.Slice(12, sizeof(uint)));
        var directoryOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(endRecord.Slice(16, sizeof(uint)));

        ulong totalEntries = totalEntries16;
        ulong directorySize = directorySize32;
        ulong directoryOffset = directoryOffset32;
        var isZip64 = totalEntries16 == ushort.MaxValue
            || entriesOnDisk == ushort.MaxValue
            || directorySize32 == uint.MaxValue
            || directoryOffset32 == uint.MaxValue;

        if (isZip64)
        {
            var endRecordAbsoluteOffset = tailOffset + endRecordIndex;
            if (endRecordAbsoluteOffset < 20)
                throw new InvalidDataException("The ZIP64 locator is missing.");

            var locator = ReadArchiveBytes(stream, endRecordAbsoluteOffset - 20, 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(0, sizeof(uint))) != zip64LocatorSignature
                || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4, sizeof(uint))) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16, sizeof(uint))) != 1)
                throw new InvalidDataException("Multi-disk or malformed ZIP64 archives are not supported.");

            var zip64RecordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, sizeof(ulong)));
            if (zip64RecordOffset > (ulong)long.MaxValue)
                throw new InvalidDataException("The ZIP64 directory offset is invalid.");

            var zip64Record = ReadArchiveBytes(stream, (long)zip64RecordOffset, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64Record.AsSpan(0, sizeof(uint))) != zip64EndOfCentralDirectorySignature)
                throw new InvalidDataException("The ZIP64 directory record is invalid.");

            var recordSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(4, sizeof(ulong)));
            if (recordSize < 44 || recordSize > (ulong)(stream.Length - (long)zip64RecordOffset - 12))
                throw new InvalidDataException("The ZIP64 directory record has an invalid size.");

            var zip64Disk = BinaryPrimitives.ReadUInt32LittleEndian(zip64Record.AsSpan(16, sizeof(uint)));
            var zip64DirectoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(zip64Record.AsSpan(20, sizeof(uint)));
            var zip64EntriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(24, sizeof(ulong)));
            totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(32, sizeof(ulong)));
            directorySize = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(40, sizeof(ulong)));
            directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(48, sizeof(ulong)));
            if (zip64Disk != 0 || zip64DirectoryDisk != 0 || zip64EntriesOnDisk != totalEntries)
                throw new InvalidDataException("Multi-disk ZIP64 archives are not supported.");
        }
        else if (diskNumber != 0 || directoryDiskNumber != 0 || entriesOnDisk != totalEntries16)
        {
            throw new InvalidDataException("Multi-disk archives are not supported.");
        }

        if (totalEntries > MaxZipEntriesToInspect || directorySize > MaxArchiveDirectoryBytes
            || directoryOffset > (ulong)stream.Length || directorySize > (ulong)stream.Length - directoryOffset)
            throw new InvalidDataException("The archive directory exceeds the safe inspection limits.");
    }

    private static byte[] ReadArchiveBytes(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
            throw new InvalidDataException("The archive directory record is outside the file bounds.");

        stream.Position = offset;
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static PackageMetadata? TryReadNuspec(ZipArchiveEntry entry)
    {
        try
        {
            var bytes = ReadBoundedEntry(entry);
            if (bytes is null)
                return null;

            using var stream = new MemoryStream(bytes, writable: false);
            var document = LoadSafeXml(stream);
            var metadata = FindElement(document.Root, "metadata");
            var id = ElementValue(metadata, "id");
            var version = ElementValue(metadata, "version");
            var title = ElementValue(metadata, "title");
            var authors = ElementValue(metadata, "authors");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(title)
                && string.IsNullOrWhiteSpace(version))
                return null;

            return new PackageMetadata
            {
                ProductName = FirstNonEmpty(title, id),
                ProductVersion = version,
                Publisher = authors,
                PackageIdentity = id
            };
        }
        catch (Exception ex) when (ex is IOException or XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static PackageMetadata? TryReadPackageJson(ZipArchiveEntry entry)
    {
        try
        {
            var bytes = ReadBoundedEntry(entry);
            if (bytes is null)
                return null;

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var name = JsonString(document.RootElement, "name");
            var version = JsonString(document.RootElement, "version");
            var author = JsonString(document.RootElement, "author");
            if (string.IsNullOrWhiteSpace(author)
                && document.RootElement.TryGetProperty("author", out var authorElement)
                && authorElement.ValueKind == JsonValueKind.Object)
                author = JsonString(authorElement, "name");

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(version))
                return null;

            return new PackageMetadata
            {
                ProductName = name,
                ProductVersion = version,
                Publisher = author,
                PackageIdentity = name
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? ReadBoundedEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxManifestBytes)
            return null;

        using var source = entry.Open();
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        var totalRead = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, MaxManifestBytes + 1 - totalRead));
            if (read == 0)
                return destination.ToArray();

            totalRead += read;
            if (totalRead > MaxManifestBytes)
                return null;

            destination.Write(buffer, 0, read);
        }
    }

    private static string JsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static XDocument LoadSafeXml(Stream stream)
    {
        if (!stream.CanSeek)
            throw new XmlException("The bounded manifest stream cannot be rewound for validation.");

        var nodeCount = 0;
        using (var validationReader = XmlReader.Create(stream, CreateSafeXmlReaderSettings()))
        {
            while (validationReader.Read())
            {
                if (++nodeCount > MaxXmlNodes || validationReader.Depth > MaxXmlDepth)
                    throw new XmlException("The manifest exceeds the safe XML node or nesting limit.");
            }
        }

        stream.Position = 0;
        using var reader = XmlReader.Create(stream, CreateSafeXmlReaderSettings());
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static XmlReaderSettings CreateSafeXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = MaxManifestBytes,
        MaxCharactersFromEntities = MaxManifestBytes
    };

    private static XElement? FindElement(XElement? parent, string localName) =>
        parent?.DescendantsAndSelf().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string ElementValue(XElement? parent, string localName) =>
        FindElement(parent, localName)?.Value.Trim() ?? string.Empty;

    private static string Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value.Trim() ?? string.Empty;

    private static bool IsLocalizedResource(string value) =>
        value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}

/// <summary>
/// Reads MSI database properties through Windows Installer's read-only database API.
/// No install, repair, or execution API is called.
/// </summary>
internal static class MsiMetadataReader
{
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;

    public static PackageMetadata Read(string filePath)
    {
        var result = MsiOpenDatabase(filePath, IntPtr.Zero, out var database);
        if (result != 0)
            return new PackageMetadata { Warning = "No se pudo abrir la base MSI en modo de solo lectura.", IsCacheable = false };

        try
        {
            var metadata = new PackageMetadata
            {
                ProductName = ReadProperty(database, "ProductName"),
                ProductVersion = ReadProperty(database, "ProductVersion"),
                Publisher = ReadProperty(database, "Manufacturer"),
                PackageIdentity = ReadProperty(database, "ProductCode")
            };
            return string.IsNullOrWhiteSpace(metadata.ProductName)
                && string.IsNullOrWhiteSpace(metadata.ProductVersion)
                && string.IsNullOrWhiteSpace(metadata.Publisher)
                ? new PackageMetadata { PackageIdentity = metadata.PackageIdentity, Warning = "El MSI no contiene las propiedades de producto esperadas." }
                : metadata;
        }
        finally
        {
            MsiCloseHandle(database);
        }
    }

    private static string ReadProperty(uint database, string propertyName)
    {
        var query = $"SELECT `Value` FROM `Property` WHERE `Property` = '{propertyName}'";
        if (MsiDatabaseOpenView(database, query, out var view) != 0)
            return string.Empty;

        try
        {
            if (MsiViewExecute(view, 0) != 0)
                return string.Empty;

            var fetchResult = MsiViewFetch(view, out var record);
            if (fetchResult == ErrorNoMoreItems || fetchResult != 0)
                return string.Empty;

            try
            {
                var probeBuffer = new StringBuilder(1);
                uint length = 0;
                var probeResult = MsiRecordGetString(record, 1, probeBuffer, ref length);
                if (probeResult != ErrorMoreData && probeResult != 0)
                    return string.Empty;

                var buffer = new StringBuilder(checked((int)length + 1));
                var capacity = (uint)buffer.Capacity;
                return MsiRecordGetString(record, 1, buffer, ref capacity) == 0 ? buffer.ToString() : string.Empty;
            }
            finally
            {
                MsiCloseHandle(record);
            }
        }
        finally
        {
            MsiCloseHandle(view);
        }
    }

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string databasePath, IntPtr persist, out uint database);

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);

    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiViewExecute")]
    private static extern uint MsiViewExecute(uint view, uint record);

    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiViewFetch")]
    private static extern uint MsiViewFetch(uint view, out uint record);

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint valueLength);

    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiCloseHandle")]
    private static extern uint MsiCloseHandle(uint handle);
}
