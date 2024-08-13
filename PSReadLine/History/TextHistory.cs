using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.PowerShell.PSConsoleReadLine;

namespace Microsoft.PowerShell.PSReadLine.History
{
    internal class TextHistory : IHistory
    {
        public string MaybeAddToHistory(string result,
            List<EditItem> edits,
            int undoEditIndex,
            bool fromDifferentSession,
            bool fromInitialRead)
        {
            bool fromHistoryFile = fromDifferentSession || fromInitialRead;
            var addToHistoryOption = GetAddToHistoryOption(result, fromHistoryFile);

            if (addToHistoryOption != AddToHistoryOption.SkipAdding)
            {
                HistoryState._previousHistoryItem = new HistoryItem
                {
                    CommandLine = result,
                    _edits = edits,
                    _undoEditIndex = undoEditIndex,
                    _editGroupStart = -1,
                    _saved = fromHistoryFile,
                    FromOtherSession = fromDifferentSession,
                    FromHistoryFile = fromInitialRead,
                };

                if (!fromHistoryFile)
                {
                    // Add to the recent history queue, which is used when querying for prediction.
                    HistoryState._recentHistory.Enqueue(result);
                    // 'MemoryOnly' indicates sensitive content in the command line
                    HistoryState._previousHistoryItem._sensitive = addToHistoryOption == AddToHistoryOption.MemoryOnly;
                    HistoryState._previousHistoryItem.StartTime = DateTime.UtcNow;
                }

                HistoryState._history.Enqueue(HistoryState._previousHistoryItem);

                HistoryState._currentHistoryIndex = HistoryState._history.Count;

                if (HistoryState._options.HistorySaveStyle == HistorySaveStyle.SaveIncrementally && !fromHistoryFile)
                {
                    IncrementalHistoryWrite();
                }
            }
            else
            {
                HistoryState._previousHistoryItem = null;
            }

            // Clear the saved line unless we used AcceptAndGetNext in which
            // case we're really still in middle of history and might want
            // to recall the saved line.
            if (HistoryState._getNextHistoryIndex == 0)
            {
                ClearSavedCurrentLine();
            }
            return result;
        }

        private AddToHistoryOption GetAddToHistoryOption(string line, bool fromHistoryFile)
        {
            // Whitespace only is useless, never add.
            if (string.IsNullOrWhiteSpace(line))
            {
                return AddToHistoryOption.SkipAdding;
            }

            // Under "no dupes" (which is on by default), immediately drop dupes of the previous line.
            if (HistoryState._options.HistoryNoDuplicates && HistoryState._history.Count > 0 &&
                string.Equals(HistoryState._history[HistoryState._history.Count - 1].CommandLine, line, StringComparison.Ordinal))
            {
                return AddToHistoryOption.SkipAdding;
            }

            if (!fromHistoryFile && HistoryState._options.AddToHistoryHandler != null)
            {
                if (HistoryState._options.AddToHistoryHandler == PSConsoleReadLineOptions.DefaultAddToHistoryHandler)
                {
                    // Avoid boxing if it's the default handler.
                    return GetDefaultAddToHistoryOption(line);
                }

                object value = HistoryState._options.AddToHistoryHandler(line);
                if (value is PSObject psObj)
                {
                    value = psObj.BaseObject;
                }

                if (value is bool boolValue)
                {
                    return boolValue ? AddToHistoryOption.MemoryAndFile : AddToHistoryOption.SkipAdding;
                }

                if (value is AddToHistoryOption enumValue)
                {
                    return enumValue;
                }

                if (value is string strValue && Enum.TryParse(strValue, out enumValue))
                {
                    return enumValue;
                }

                // 'TryConvertTo' incurs exception handling when the value cannot be converted to the target type.
                // It's expensive, especially when we need to process lots of history items from file during the
                // initialization. So do the conversion as the last resort.
                if (LanguagePrimitives.TryConvertTo(value, out enumValue))
                {
                    return enumValue;
                }
            }

            // Add to both history queue and file by default.
            return AddToHistoryOption.MemoryAndFile;
        }

        private void IncrementalHistoryWrite()
        {
            var i = HistoryState._currentHistoryIndex - 1;
            while (i >= 0)
            {
                if (HistoryState._history[i]._saved)
                {
                    break;
                }
                i -= 1;
            }

            WriteHistoryRange(i + 1, HistoryState._history.Count - 1, overwritten: false);
        }

        private void WriteHistoryRange(int start, int end, bool overwritten)
        {
            WithHistoryFileMutexDo(100, () =>
            {
                bool retry = true;
                // Get the new content since the last sync.
                List<string> historyLines = overwritten ? null : ReadHistoryFileIncrementally();

                try
                {
                retry_after_creating_directory:
                    try
                    {
                        using (var file = overwritten ? File.CreateText(HistoryState._options.HistorySavePath) : File.AppendText(HistoryState._options.HistorySavePath))
                        {
                            for (var i = start; i <= end; i++)
                            {
                                HistoryItem item = HistoryState._history[i];
                                item._saved = true;

                                // Actually, skip writing sensitive items to file.
                                if (item._sensitive) { continue; }

                                var line = item.CommandLine.Replace("\n", "`\n");
                                file.WriteLine(line);
                            }
                        }
                        var fileInfo = new FileInfo(HistoryState._options.HistorySavePath);
                        HistoryState._historyFileLastSavedSize = fileInfo.Length;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Try making the directory, but just once
                        if (retry)
                        {
                            retry = false;
                            Directory.CreateDirectory(Path.GetDirectoryName(HistoryState._options.HistorySavePath));
                            goto retry_after_creating_directory;
                        }
                    }
                }
                finally
                {
                    if (historyLines != null)
                    {
                        // Populate new history from other sessions to the history queue after we are done
                        // with writing the specified range to the file.
                        // We do it at this point to make sure the range of history items from 'start' to
                        // 'end' do not get changed before the writing to the file.
                        UpdateHistoryFromFile(historyLines, fromDifferentSession: true, fromInitialRead: false);
                    }
                }
            });
        }

        private bool WithHistoryFileMutexDo(int timeout, Action action)
        {
            int retryCount = 0;
            do
            {
                try
                {
                    if (HistoryState._historyFileMutex.WaitOne(timeout))
                    {
                        try
                        {
                            action();
                            return true;
                        }
                        catch (UnauthorizedAccessException uae)
                        {
                            ReportHistoryFileError(uae);
                            return false;
                        }
                        catch (IOException ioe)
                        {
                            ReportHistoryFileError(ioe);
                            return false;
                        }
                        finally
                        {
                            HistoryState._historyFileMutex.ReleaseMutex();
                        }
                    }

                    // Consider it a failure if we timed out on the mutex.
                    return false;
                }
                catch (AbandonedMutexException)
                {
                    retryCount += 1;

                    // We acquired the mutex object that was abandoned by another powershell process.
                    // Now, since we own it, we must release it before retry, otherwise, we will miss
                    // a release and keep holding the mutex, in which case the 'WaitOne' calls from
                    // all other powershell processes will time out.
                    HistoryState._historyFileMutex.ReleaseMutex();
                }
            } while (retryCount > 0 && retryCount < 3);

            // If we reach here, that means we've done the retries but always got the 'AbandonedMutexException'.
            return false;
        }

        private int historyErrorReportedCount;
        private void ReportHistoryFileError(Exception e)
        {
            if (historyErrorReportedCount == 2)
                return;

            historyErrorReportedCount += 1;
            Console.Write(HistoryState._options._errorColor);
            Console.WriteLine(PSReadLineResources.HistoryFileErrorMessage, HistoryState._options.HistorySavePath, e.Message);
            if (historyErrorReportedCount == 2)
            {
                Console.WriteLine(PSReadLineResources.HistoryFileErrorFinalMessage);
            }
            Console.Write("\x1b0m");
        }

        private void ClearSavedCurrentLine()
        {
            HistoryState._savedCurrentLine.CommandLine = null;
            HistoryState._savedCurrentLine._edits = null;
            HistoryState._savedCurrentLine._undoEditIndex = 0;
            HistoryState._savedCurrentLine._editGroupStart = -1;
        }

        private List<string> ReadHistoryFileIncrementally()
        {
            var fileInfo = new FileInfo(HistoryState._options.HistorySavePath);
            if (fileInfo.Exists && fileInfo.Length != HistoryState._historyFileLastSavedSize)
            {
                var historyLines = new List<string>();
                using (var fs = new FileStream(HistoryState._options.HistorySavePath, FileMode.Open))
                using (var sr = new StreamReader(fs))
                {
                    fs.Seek(HistoryState._historyFileLastSavedSize, SeekOrigin.Begin);

                    while (!sr.EndOfStream)
                    {
                        historyLines.Add(sr.ReadLine());
                    }
                }

                HistoryState._historyFileLastSavedSize = fileInfo.Length;
                return historyLines.Count > 0 ? historyLines : null;
            }

            return null;
        }

        void UpdateHistoryFromFile(IEnumerable<string> historyLines, bool fromDifferentSession, bool fromInitialRead)
        {
            var sb = new StringBuilder();
            foreach (var line in historyLines)
            {
                if (line.EndsWith("`", StringComparison.Ordinal))
                {
                    sb.Append(line, 0, line.Length - 1);
                    sb.Append('\n');
                }
                else if (sb.Length > 0)
                {
                    sb.Append(line);
                    var l = sb.ToString();
                    var editItems = new List<EditItem> { EditItemInsertString.Create(l, 0) };
                    MaybeAddToHistory(l, editItems, 1, fromDifferentSession, fromInitialRead);
                    sb.Clear();
                }
                else
                {
                    var editItems = new List<EditItem> { EditItemInsertString.Create(line, 0) };
                    MaybeAddToHistory(line, editItems, 1, fromDifferentSession, fromInitialRead);
                }
            }
        }

    }

}
