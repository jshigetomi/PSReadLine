using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using static Microsoft.PowerShell.PSConsoleReadLine;
using System.Text.RegularExpressions;

namespace Microsoft.PowerShell.PSReadLine.History
{
    internal class HistoryContext
    {
        // History state
        internal HistoryQueue<HistoryItem> _history;
        internal HistoryQueue<string> _recentHistory;
        internal HistoryItem _previousHistoryItem;
        internal Dictionary<string, int> _hashedHistory;
        internal int _currentHistoryIndex;
        internal int _getNextHistoryIndex;
        internal int _searchHistoryCommandCount;
        internal int _recallHistoryCommandCount;
        internal int _anyHistoryCommandCount;
        internal string _searchHistoryPrefix;
        // When cycling through history, the current line (not yet added to history)
        // is saved here so it can be restored.
        internal readonly HistoryItem _savedCurrentLine;

        // Reference to options
        internal PSConsoleReadLineOptions _options;

        internal Mutex _historyFileMutex;
        internal long _historyFileLastSavedSize;

        internal const string _forwardISearchPrompt = "fwd-i-search: ";
        internal const string _backwardISearchPrompt = "bck-i-search: ";
        internal const string _failedForwardISearchPrompt = "failed-fwd-i-search: ";
        internal const string _failedBackwardISearchPrompt = "failed-bck-i-search: ";

        // Pattern used to check for sensitive inputs.
        internal static readonly Regex s_sensitivePattern = new Regex(
            "password|asplaintext|token|apikey|secret",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static readonly HashSet<string> s_SecretMgmtCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "Get-Secret",
            "Get-SecretInfo",
            "Get-SecretVault",
            "Register-SecretVault",
            "Remove-Secret",
            "Set-SecretInfo",
            "Set-SecretVaultDefault",
            "Test-SecretVault",
            "Unlock-SecretVault",
            "Unregister-SecretVault",
            "Get-AzAccessToken",
        };
        internal HistoryContext(HistoryItem savedCurrentLine, PSConsoleReadLineOptions options)
        {
            _savedCurrentLine = savedCurrentLine;
            _options = options;
        }
    }
}
