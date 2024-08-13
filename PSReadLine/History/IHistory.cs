using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.PowerShell.PSConsoleReadLine;

namespace Microsoft.PowerShell.PSReadLine.History
{
    internal interface IHistory
    {
        string MaybeAddToHistory(string command,
            List<EditItem> editItems,
            int undoEditIndex,
            bool fromDifferentSession = false,
            bool fromInitialRead = false);
    }
}
