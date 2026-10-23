
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

class Program
{
    // CHANGE THIS TO YOUR ROOT FOLDER
    static readonly string RootFolder =
        @"C:\Your\Root\Folder";

    // false = preview only
    // true = modify SQL files
    static readonly bool ApplyChanges = false;

    static readonly Regex EchoRegex = new Regex(
        @"(?im)^[ \t]*SET[ \t]+ECHO[ \t]+ON[ \t]*;?[ \t]*$");

    static readonly Regex SpoolRegex = new Regex(
        @"(?im)^[ \t]*SPOOL[ \t]+OFF[ \t]*;?[ \t]*$");

    static int processed = 0;
    static int updated = 0;
    static int skipped = 0;
    static int errors = 0;

    static readonly List<string> Report =
        new List<string>();

    static void Main()
    {
        if (!Directory.Exists(RootFolder))
        {
            Console.WriteLine("Root folder not found.");
            return;
        }

        string reportFolder = Path.Combine(
            RootFolder, "_SQL_AUTOMATION_REPORT");

        Directory.CreateDirectory(reportFolder);

        string backupFolder = Path.Combine(
            reportFolder, "Backup");

        if (ApplyChanges)
            Directory.CreateDirectory(backupFolder);

        string[] files = Directory.GetFiles(
            RootFolder,
            "*.sql",
            SearchOption.AllDirectories);

        foreach (string file in files)
        {
            // Do not process our backup folder
            if (file.StartsWith(
                reportFolder + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                continue;

            processed++;

            try
            {
                ProcessFile(file, backupFolder);
            }
            catch (Exception ex)
            {
                errors++;
                AddReport(file, "ERROR", ex.Message);
                Console.WriteLine(
                    "ERROR: " + file + " - " + ex.Message);
            }
        }

        string reportPath = Path.Combine(
            reportFolder,
            "SQL_Automation_Report_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss") +
            ".csv");

        File.WriteAllLines(
            reportPath,
            new[] { "File,Status,Details" }
                .Concat(Report),
            Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine("========== SUMMARY ==========");
        Console.WriteLine("Mode      : " +
            (ApplyChanges ? "APPLY" : "PREVIEW"));
        Console.WriteLine("Processed : " + processed);
        Console.WriteLine("Updated   : " + updated);
        Console.WriteLine("Skipped   : " + skipped);
        Console.WriteLine("Errors    : " + errors);
        Console.WriteLine("Report    : " + reportPath);
        Console.WriteLine("=============================");

        Console.ReadLine();
    }

    static void ProcessFile(
        string file, string backupFolder)
    {
        string relativePath = file.Substring(
            RootFolder.TrimEnd('\\', '/') .Length)
            .TrimStart('\\', '/');

        string[] folders = relativePath.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        // Find numbered folder such as 001 or 225
        string folder = null;

        foreach (string part in folders.Take(folders.Length - 1))
        {
            if (Regex.IsMatch(part, @"^\d{3}$"))
                folder = part;
        }

        if (folder == null)
        {
            skipped++;
            AddReport(file, "SKIPPED",
                "Numbered folder not found");
            return;
        }

        string packageName = "CCMU_" + folder;

        string queryName =
            Path.GetFileNameWithoutExtension(file);

        byte[] originalBytes = File.ReadAllBytes(file);

        Encoding encoding;
        int bomLength;

        if (!TryDetectEncoding(
            originalBytes, out encoding, out bomLength))
        {
            skipped++;
            AddReport(file, "SKIPPED",
                "Encoding could not be safely identified");
            return;
        }

        string originalSql = encoding.GetString(
            originalBytes,
            bomLength,
            originalBytes.Length - bomLength);

        if (originalSql.Contains("-- AUTO_LOG_START") ||
            originalSql.Contains("-- AUTO_LOG_END"))
        {
            skipped++;
            AddReport(file, "SKIPPED",
                "Logging markers already exist");
            return;
        }

        Match echoMatch = EchoRegex.Match(originalSql);
        MatchCollection spoolMatches =
            SpoolRegex.Matches(originalSql);

        if (!echoMatch.Success || spoolMatches.Count == 0)
        {
            skipped++;
            AddReport(file, "SKIPPED",
                "SET ECHO ON or SPOOL OFF missing");
            return;
        }

        Match spoolMatch =
            spoolMatches[spoolMatches.Count - 1];

        if (spoolMatch.Index <= echoMatch.Index)
        {
            skipped++;
            AddReport(file, "SKIPPED",
                "Invalid insertion positions");
            return;
        }

        string newline = originalSql.Contains("\r\n")
            ? "\r\n" : "\n";

        string startInsert = BuildStartInsert(
            packageName, queryName);

        string endInsert = BuildEndInsert(
            packageName, queryName);

        // Insert from bottom to top to preserve offsets
        string modifiedSql = originalSql.Insert(
            spoolMatch.Index,
            "-- AUTO_LOG_END" + newline +
            endInsert + newline);

        modifiedSql = modifiedSql.Insert(
            echoMatch.Index + echoMatch.Length,
            newline +
            "-- AUTO_LOG_START" + newline +
            startInsert);

        if (!ApplyChanges)
        {
            updated++;
            AddReport(file, "PREVIEW",
                packageName + " | " + queryName);
            Console.WriteLine("PREVIEW: " + file);
            return;
        }

        // Create backup preserving directory structure
        string backupPath = Path.Combine(
            backupFolder, relativePath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(backupPath));

        // Never overwrite an existing backup
        if (File.Exists(backupPath))
        {
            skipped++;
            AddReport(file, "SKIPPED",
                "Backup already exists");
            return;
        }

        File.Copy(file, backupPath, false);

        byte[] body = encoding.GetBytes(modifiedSql);
        byte[] bom = encoding.GetPreamble();

        byte[] output = new byte[bom.Length + body.Length];

        Buffer.BlockCopy(
            bom, 0, output, 0, bom.Length);

        Buffer.BlockCopy(
            body, 0, output, bom.Length, body.Length);

        string tempFile = file + ".tmp";

        try
        {
            File.WriteAllBytes(tempFile, output);

            // Replace existing SQL file
            File.Replace(tempFile, file, null);

            updated++;
            AddReport(file, "UPDATED",
                packageName + " | " + queryName);

            Console.WriteLine("UPDATED: " + file);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    static string BuildStartInsert(
        string packageName, string queryName)
    {
        // REPLACE WITH YOUR ACTUAL START INSERT SQL
        return
            "INSERT INTO YOUR_LOG_TABLE " +
            "(PACKAGE_NAME, QUERY_NAME, STATUS) VALUES (" +
            "'" + EscapeSql(packageName) + "', " +
            "'" + EscapeSql(queryName) + "', " +
            "'START');";
    }

    static string BuildEndInsert(
        string packageName, string queryName)
    {
        // REPLACE WITH YOUR ACTUAL END INSERT SQL
        return
            "INSERT INTO YOUR_LOG_TABLE " +
            "(PACKAGE_NAME, QUERY_NAME, STATUS) VALUES (" +
            "'" + EscapeSql(packageName) + "', " +
            "'" + EscapeSql(queryName) + "', " +
            "'END');";
    }

    static string EscapeSql(string value)
    {
        return value.Replace("'", "''");
    }

    static bool TryDetectEncoding(
        byte[] bytes,
        out Encoding encoding,
        out int bomLength)
    {
        bomLength = 0;
        encoding = null;

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(false, true);
            bomLength = 3;
            return true;
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(
                false, true, true);
            bomLength = 2;
            return true;
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(
                true, true, true);
            bomLength = 2;
            return true;
        }

        // Strict UTF-8 validation
        try
        {
            Encoding utf8 = new UTF8Encoding(false, true);
            utf8.GetString(bytes);
            encoding = utf8;
            return true;
        }
        catch (DecoderFallbackException)
        {
            // Could be Windows-1252 or another legacy
            // encoding. Do not guess during bulk updates.
            return false;
        }
    }

    static void AddReport(
        string file, string status, string details)
    {
        Report.Add(
            Csv(file) + "," +
            Csv(status) + "," +
            Csv(details));
    }

    static string Csv(string value)
    {
        return "\"" +
            (value ?? "").Replace("\"", "\"\"") +
            "\"";
    }
}
