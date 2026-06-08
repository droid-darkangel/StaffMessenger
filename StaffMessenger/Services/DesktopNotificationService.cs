using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StaffMessenger.Services;

public static class DesktopNotificationService
{
    public static async Task ShowAsync(string title, string body)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                await RunAsync("osascript", [
                    "-e",
                    $"display notification {AppleScriptQuote(body)} with title {AppleScriptQuote(title)}"
                ]);
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await RunAsync("powershell", [
                    "-NoProfile",
                    "-Command",
                    $"[reflection.assembly]::loadwithpartialname('System.Windows.Forms') | out-null; $n = new-object system.windows.forms.notifyicon; $n.icon = [system.drawing.systemicons]::information; $n.visible = $true; $n.showballoontip(4000, {PowerShellQuote(title)}, {PowerShellQuote(body)}, [system.windows.forms.tooltipicon]::None)"
                ]);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        await process.WaitForExitAsync();
    }

    private static string AppleScriptQuote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string PowerShellQuote(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
