using System.Security.Cryptography;

namespace RltUdpClient.Core;

/// <summary>
/// A random id, made on first run and kept on disk, so the usage counter can
/// tell one machine checking twice from two machines. It is not derived from
/// anything about the machine or the person.
/// </summary>
public static class InstallId
{
    private const string FileName = ".install-id";

    /// <summary>
    /// Reads the id from the first folder that has one, or writes a new one to
    /// the first folder that accepts it. The config folder comes first; the
    /// output folder is the fallback for installs like the systemd service,
    /// where the config sits in a read-only /etc. If nothing is writable the id
    /// lives only as long as the process, which costs an overcount, not a failure.
    /// </summary>
    public static string Load(params string?[] folders)
    {
        var candidates = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToArray();

        foreach (var folder in candidates)
        {
            try
            {
                var existing = File.ReadAllText(Path.Combine(folder, FileName)).Trim();
                if (IsValid(existing))
                    return existing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not there or not readable; try the next folder.
            }
        }

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        foreach (var folder in candidates)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, FileName), id);
                return id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only; try the next folder.
            }
        }

        return id;
    }

    private static bool IsValid(string id) =>
        id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
