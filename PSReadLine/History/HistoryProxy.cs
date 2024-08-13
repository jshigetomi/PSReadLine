using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.PowerShell.PSConsoleReadLine;

namespace Microsoft.PowerShell.PSReadLine.History
{
    public static class HistoryProxy
    {
        private static IHistory _historyType;

        internal static void SetHistoryType(IHistory historyType)
        {
            _historyType = historyType;
        }

        public static string AddToHistoryProxy(string command, List<EditItem> editItems, int undoEditIndex, bool fromDifferentSession,
            bool fromInitialRead)
        {
            return _historyType.MaybeAddToHistory(command, editItems, undoEditIndex, fromDifferentSession);
        }
    }
}
