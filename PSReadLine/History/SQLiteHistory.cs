using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.PowerShell.PSConsoleReadLine;

namespace Microsoft.PowerShell.PSReadLine.History
{
    internal class SQLiteHistory : IHistory
    {
        public string MaybeAddToHistory(string result,
            List<EditItem> edits,
            int undoEditIndex,
            bool fromDifferentSession = false,
            bool fromInitialRead = false)
        {
            return "template";
        }
    }
}
