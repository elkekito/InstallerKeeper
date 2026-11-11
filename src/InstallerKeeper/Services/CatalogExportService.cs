using System.IO;
using System.Text;
using System.Text.Json;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

public static class CatalogExportService
{
    public static void ExportToCsv(IEnumerable<InstallerEntry> entries, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"Nombre\",\"Versión local\",\"Versión oficial\",\"Estado\",\"Tipo\",\"Arquitectura\",\"Editor\",\"WinGet ID\",\"Tamaño (Bytes)\",\"Tamaño formateado\",\"Ruta local\",\"URL oficial\",\"SHA-256\"");

        foreach (var entry in entries)
        {
            long fileSizeBytes = 0;
            if (!string.IsNullOrWhiteSpace(entry.LocalFilePath) && File.Exists(entry.LocalFilePath))
            {
                try { fileSizeBytes = new FileInfo(entry.LocalFilePath).Length; } catch { }
            }

            sb.AppendLine(string.Join(",",
                EscapeCsv(entry.Name),
                EscapeCsv(entry.CurrentVersion),
                EscapeCsv(entry.OfficialVersion),
                EscapeCsv(entry.Status),
                EscapeCsv(entry.PackageType),
                EscapeCsv(entry.Architecture),
                EscapeCsv(entry.Publisher),
                EscapeCsv(entry.WingetId ?? string.Empty),
                fileSizeBytes.ToString(),
                EscapeCsv(entry.FormattedFileSize),
                EscapeCsv(entry.LocalFilePath ?? string.Empty),
                EscapeCsv(entry.OfficialInstallerUrl),
                EscapeCsv(entry.OfficialSha256)
            ));
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    public static void ExportToJson(IEnumerable<InstallerEntry> entries, string filePath)
    {
        var exportList = entries.Select(entry =>
        {
            long fileSizeBytes = 0;
            if (!string.IsNullOrWhiteSpace(entry.LocalFilePath) && File.Exists(entry.LocalFilePath))
            {
                try { fileSizeBytes = new FileInfo(entry.LocalFilePath).Length; } catch { }
            }

            return new
            {
                name = entry.Name,
                currentVersion = entry.CurrentVersion,
                officialVersion = entry.OfficialVersion,
                status = entry.Status,
                packageType = entry.PackageType,
                architecture = entry.Architecture,
                publisher = entry.Publisher,
                wingetId = entry.WingetId,
                isIdentified = entry.IsIdentified,
                isExcluded = entry.IsExcluded,
                isHidden = entry.IsHidden,
                fileSizeBytes = fileSizeBytes,
                formattedFileSize = entry.FormattedFileSize,
                localFilePath = entry.LocalFilePath,
                officialInstallerUrl = entry.OfficialInstallerUrl,
                officialSha256 = entry.OfficialSha256,
                officialPublisher = entry.OfficialPublisher
            };
        }).ToArray();

        var payload = new
        {
            app = AppInfo.Name,
            version = AppInfo.Version,
            exportedAtUtc = DateTime.UtcNow.ToString("o"),
            totalCount = exportList.Length,
            items = exportList
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json, new UTF8Encoding(true));
    }

    public static void ExportToHtml(IEnumerable<InstallerEntry> entries, string filePath)
    {
        var list = entries.ToList();
        var total = list.Count;
        var updates = list.Count(e => e.Status == InstallerStatus.NewVersion);
        var upToDate = list.Count(e => e.Status == InstallerStatus.UpToDate);
        var unknown = total - updates - upToDate;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"es\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"  <title>Catálogo de Instaladores - {AppInfo.Name}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --bg: #f8fafc; --card: #ffffff; --text: #0f172a; --muted: #64748b; --border: #e2e8f0; --primary: #0284c7; --success: #16a34a; --warning: #d97706; }");
        sb.AppendLine("    @media (prefers-color-scheme: dark) { :root { --bg: #0f172a; --card: #1e293b; --text: #f8fafc; --muted: #94a3b8; --border: #334155; --primary: #38bdf8; --success: #22c55e; --warning: #f59e0b; } }");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: var(--bg); color: var(--text); margin: 0; padding: 32px 24px; }");
        sb.AppendLine("    .container { max-width: 1300px; margin: 0 auto; }");
        sb.AppendLine("    header { margin-bottom: 28px; }");
        sb.AppendLine("    h1 { margin: 0 0 6px 0; font-size: 26px; display: flex; align-items: center; gap: 10px; }");
        sb.AppendLine("    .meta { color: var(--muted); font-size: 14px; }");
        sb.AppendLine("    .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 14px; margin-bottom: 24px; }");
        sb.AppendLine("    .stat-card { background: var(--card); border: 1px solid var(--border); border-radius: 10px; padding: 16px 20px; }");
        sb.AppendLine("    .stat-num { font-size: 24px; font-weight: bold; margin-bottom: 4px; }");
        sb.AppendLine("    .stat-label { color: var(--muted); font-size: 13px; }");
        sb.AppendLine("    .table-container { background: var(--card); border: 1px solid var(--border); border-radius: 10px; overflow-x: auto; box-shadow: 0 1px 3px rgba(0,0,0,0.05); }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; font-size: 13px; text-align: left; }");
        sb.AppendLine("    th { background: rgba(0,0,0,0.02); padding: 12px 14px; border-bottom: 1px solid var(--border); font-weight: 600; color: var(--muted); }");
        sb.AppendLine("    td { padding: 12px 14px; border-bottom: 1px solid var(--border); vertical-align: middle; }");
        sb.AppendLine("    tr:last-child td { border-bottom: none; }");
        sb.AppendLine("    tr:hover { background: rgba(0,0,0,0.015); }");
        sb.AppendLine("    .badge { display: inline-block; padding: 3px 8px; border-radius: 6px; font-size: 11px; font-weight: 600; }");
        sb.AppendLine("    .badge-update { background: #fef3c7; color: #92400e; }");
        sb.AppendLine("    .badge-uptodate { background: #dcfce7; color: #166534; }");
        sb.AppendLine("    .badge-unknown { background: #f1f5f9; color: #475569; }");
        sb.AppendLine("    .code { font-family: Consolas, Monaco, monospace; font-size: 12px; }");
        sb.AppendLine("    .path { max-width: 260px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"container\">");
        sb.AppendLine("    <header>");
        sb.AppendLine($"      <h1>📦 Catálogo de Instaladores</h1>");
        sb.AppendLine($"      <div class=\"meta\">Generado por {AppInfo.Name} v{AppInfo.Version} el {DateTime.Now:dd/MM/yyyy HH:mm:ss}</div>");
        sb.AppendLine("    </header>");
        sb.AppendLine("    <div class=\"stats\">");
        sb.AppendLine($"      <div class=\"stat-card\"><div class=\"stat-num\">{total}</div><div class=\"stat-label\">Total aplicaciones</div></div>");
        sb.AppendLine($"      <div class=\"stat-card\"><div class=\"stat-num\" style=\"color: var(--warning);\">{updates}</div><div class=\"stat-label\">Actualizaciones disponibles</div></div>");
        sb.AppendLine($"      <div class=\"stat-card\"><div class=\"stat-num\" style=\"color: var(--success);\">{upToDate}</div><div class=\"stat-label\">Al día</div></div>");
        sb.AppendLine($"      <div class=\"stat-card\"><div class=\"stat-num\">{unknown}</div><div class=\"stat-label\">Sin comprobar / Otros</div></div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"table-container\">");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <thead>");
        sb.AppendLine("          <tr>");
        sb.AppendLine("            <th>Nombre</th>");
        sb.AppendLine("            <th>Versión local</th>");
        sb.AppendLine("            <th>Versión oficial</th>");
        sb.AppendLine("            <th>Estado</th>");
        sb.AppendLine("            <th>Tipo</th>");
        sb.AppendLine("            <th>Editor</th>");
        sb.AppendLine("            <th>WinGet ID</th>");
        sb.AppendLine("            <th>Tamaño</th>");
        sb.AppendLine("            <th>Ruta del archivo</th>");
        sb.AppendLine("          </tr>");
        sb.AppendLine("        </thead>");
        sb.AppendLine("        <tbody>");

        foreach (var entry in list)
        {
            var badgeClass = entry.Status == InstallerStatus.NewVersion
                ? "badge-update"
                : entry.Status == InstallerStatus.UpToDate
                    ? "badge-uptodate"
                    : "badge-unknown";

            sb.AppendLine("          <tr>");
            sb.AppendLine($"            <td><strong>{HtmlEncode(entry.Name)}</strong></td>");
            sb.AppendLine($"            <td><span class=\"code\">{HtmlEncode(entry.CurrentVersion)}</span></td>");
            sb.AppendLine($"            <td><span class=\"code\">{HtmlEncode(entry.OfficialVersion)}</span></td>");
            sb.AppendLine($"            <td><span class=\"badge {badgeClass}\">{HtmlEncode(entry.Status)}</span></td>");
            sb.AppendLine($"            <td>{HtmlEncode(entry.PackageType)}</td>");
            sb.AppendLine($"            <td>{HtmlEncode(entry.Publisher)}</td>");
            sb.AppendLine($"            <td><span class=\"code\">{HtmlEncode(entry.WingetId ?? "-")}</span></td>");
            sb.AppendLine($"            <td>{HtmlEncode(entry.FormattedFileSize)}</td>");
            sb.AppendLine($"            <td class=\"path\" title=\"{HtmlEncode(entry.LocalFilePath ?? "")}\">{HtmlEncode(Path.GetFileName(entry.LocalFilePath ?? ""))}</td>");
            sb.AppendLine("          </tr>");
        }

        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        // Excel evalúa como fórmula un campo que empiece así; se antepone un apóstrofo para tratarlo como texto.
        var safe = value.Length > 1 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private static string HtmlEncode(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return System.Net.WebUtility.HtmlEncode(value);
    }
}
