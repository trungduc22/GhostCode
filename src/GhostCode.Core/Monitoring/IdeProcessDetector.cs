using System.Diagnostics;

namespace GhostCode.Core.Monitoring;

public sealed record ActiveIdeStatus(
    bool DevCpp,
    bool VsCode,
    bool Python,
    bool Edge,
    bool Thorium)
{
    public string ActiveIdeSummary
    {
        get
        {
            var list = new List<string>();
            if (DevCpp) list.Add("Dev-C++");
            if (VsCode) list.Add("VS Code");
            if (Python) list.Add("Python");
            return list.Count == 0 ? "None" : string.Join(", ", list);
        }
    }
}

public static class IdeProcessDetector
{
    public static ActiveIdeStatus Detect()
    {
        bool devcpp = false, vscode = false, python = false, edge = false, thorium = false;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName.ToLowerInvariant();
                    switch (name)
                    {
                        case "devcpp":
                            devcpp = true;
                            break;
                        case "code":
                            vscode = true;
                            break;
                        case "python" or "pythonw":
                            python = true;
                            break;
                        case "msedge":
                            edge = true;
                            break;
                        case "thorium":
                            thorium = true;
                            break;
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }

        return new ActiveIdeStatus(devcpp, vscode, python, edge, thorium);
    }
}
