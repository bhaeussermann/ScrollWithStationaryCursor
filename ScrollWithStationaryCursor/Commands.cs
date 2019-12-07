﻿using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Design;
using System.Linq;
using Task = System.Threading.Tasks.Task;

namespace ScrollWithStationaryCursor
{
    internal sealed class Commands
    {
        private const int UpCommandId = 0x0100, DownCommandId = 0x0101;
        private const int UpExtendCommandId = 0x0102, DownExtendCommandId = 0x0103;
        private const int VerticalScrollBar = 1;
        private const int DirectionUp = -1, DirectionDown = 1;
        
        private readonly AsyncPackage package;
        
        private Commands(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            RegisterCommand(UpCommandId, ExecuteUp, commandService);
            RegisterCommand(DownCommandId, ExecuteDown, commandService);
            RegisterCommand(UpExtendCommandId, ExecuteUpExtend, commandService);
            RegisterCommand(DownExtendCommandId, ExecuteDownExtend, commandService);
        }
        
        public static Commands Instance { get; private set; }
        
        private IServiceProvider ServiceProvider => this.package;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new Commands(package, commandService);
        }

        private void RegisterCommand(int commandId, EventHandler commandDelegate, OleMenuCommandService commandService)
        {
            var menuCommandId = new CommandID(Package.CommandSetGuid, commandId);
            var menuItem = new MenuCommand(commandDelegate, menuCommandId);
            commandService.AddCommand(menuItem);
        }
        
        private void ExecuteUp(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Scroll(DirectionUp);
        }

        private void ExecuteDown(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Scroll(DirectionDown);
        }

        private void ExecuteUpExtend(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Scroll(DirectionUp, extendSelection: true);
        }

        private void ExecuteDownExtend(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Scroll(DirectionDown, extendSelection: true);
        }

        private void Scroll(int direction, bool extendSelection = false)
        {
            var textManager = (IVsTextManager2)ServiceProvider.GetService(typeof(SVsTextManager));
            if ((textManager != null)
                && (textManager.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftCodeWindow, out var view) == VSConstants.S_OK)
                && (view.GetScrollInfo(VerticalScrollBar, out int _, out int _, out int _, out int top) == VSConstants.S_OK)
                && (view.GetCaretPos(out int caretLine, out int caretColumn) == VSConstants.S_OK))
            {
                int nextLine = caretLine + direction;
                bool isNextLineValid = ((direction == DirectionUp) && (top > 0))
                    || ((direction == DirectionDown) && (view.GetPointOfLineColumn(nextLine, caretColumn, new Microsoft.VisualStudio.OLE.Interop.POINT[1]) == VSConstants.S_OK));
                if (!isNextLineValid)
                    return;

                if (SkipCollapsedRegionForCaretToTheRightOfCollapsedRegion(view, direction, caretLine, caretColumn, out int nextLineForCase1))
                {
                    nextLine = nextLineForCase1;
                }
                else if (SkipCollapsedRegion(view, direction, caretLine, caretColumn, out int nextLineForCase2))
                {
                    nextLine = nextLineForCase2;
                }

                var textViewManager = TextViewManager.Instance;
                var textSnapshot = textViewManager.ActiveTextView.TextSnapshot;
                if ((view.GetNearestPosition(caretLine, caretColumn, out int currentPosition, out int _) != VSConstants.S_OK)
                    || (view.GetNearestPosition(nextLine, caretColumn, out int nextPosition, out int _) != VSConstants.S_OK))
                    return;
                var currentPoint = new SnapshotPoint(textSnapshot, currentPosition);
                var nextLinePoint = new SnapshotPoint(textSnapshot, nextPosition);
                var nextTextViewLine = textViewManager.ActiveTextView.GetTextViewLineContainingBufferPosition(nextLinePoint);
                textViewManager.ActiveTextView.Caret.MoveTo(nextTextViewLine);

                if ((view.GetCaretPos(out caretLine, out caretColumn) == VSConstants.S_OK)
                    && (view.GetNearestPosition(caretLine, caretColumn, out currentPosition, out int _) == VSConstants.S_OK))
                {
                    var previousPoint = currentPoint;
                    currentPoint = new SnapshotPoint(textSnapshot, currentPosition);
                    UpdateSelection(previousPoint, currentPoint, extendSelection);
                }

                view.SetScrollPosition(VerticalScrollBar, top + direction);
            }
        }

        private static bool SkipCollapsedRegionForCaretToTheRightOfCollapsedRegion(
            IVsTextView view, int direction, int caretLine, int caretColumn, out int nextLine)
        {
            nextLine = 0;
            if ((view.GetNearestPosition(caretLine, caretColumn, out int caretPosition, out int _) != VSConstants.S_OK) || (caretPosition == 0))
                return false;

            var textViewManager = TextViewManager.Instance;
            var outliningManager = textViewManager.GetOutliningManager(textViewManager.ActiveTextView);
            var textSnapshot = textViewManager.ActiveTextView.TextSnapshot;

            var leftOfCaretPositionPoint = new SnapshotPoint(textSnapshot, caretPosition - 1);
            var leftOfCaretPositionSpan = textViewManager.ActiveTextView.GetTextElementSpan(leftOfCaretPositionPoint);
            var collapsedRegion = outliningManager.GetCollapsedRegions(leftOfCaretPositionSpan).FirstOrDefault();
            if (collapsedRegion == null)
                return false;

            var endPoint = collapsedRegion.Extent.GetEndPoint(textSnapshot);
            bool caretIsAtTheEndOfCollapsedRegion = endPoint.Position == caretPosition;
            if (!caretIsAtTheEndOfCollapsedRegion)
                return false;

            if (view.GetLineAndColumn(endPoint.Position, out int regionEndLine, out int _) != VSConstants.S_OK)
                return false;

            bool regionEndsAtEndOfLine =
                (view.GetLineAndColumn(endPoint.Position + 2, out int lineOfPositionAfterRegionEnd, out int _) == VSConstants.S_OK)
                && (lineOfPositionAfterRegionEnd > regionEndLine);
            if (!regionEndsAtEndOfLine)
                return false;

            var startPoint = collapsedRegion.Extent.GetStartPoint(textSnapshot);
            if (view.GetLineAndColumn(startPoint.Position, out int regionStartLine, out int _) != VSConstants.S_OK)
                return false;

            nextLine = direction == DirectionDown ? caretLine + 1 : regionStartLine - 1;
            return true;
        }

        private static bool SkipCollapsedRegion(
            IVsTextView view, int direction, int caretLine, int caretColumn, out int nextLine)
        {
            nextLine = caretLine + direction;
            if (view.GetNearestPosition(nextLine, caretColumn, out int nextPosition, out int _) != VSConstants.S_OK)
                return false;

            var textViewManager = TextViewManager.Instance;
            var outliningManager = textViewManager.GetOutliningManager(textViewManager.ActiveTextView);
            var textSnapshot = textViewManager.ActiveTextView.TextSnapshot;

            var nextPositionPoint = new SnapshotPoint(textSnapshot, nextPosition);
            var nextPositionSpan = textViewManager.ActiveTextView.GetTextElementSpan(nextPositionPoint);
            var collapsedRegion = outliningManager.GetCollapsedRegions(nextPositionSpan).FirstOrDefault();
            if (collapsedRegion == null)
                return false;

            var startPoint = collapsedRegion.Extent.GetStartPoint(textSnapshot);
            var endPoint = collapsedRegion.Extent.GetEndPoint(textSnapshot);
            if ((view.GetLineAndColumn(startPoint.Position, out int regionStartLine, out int regionStartColumn) != VSConstants.S_OK)
                || (view.GetLineAndColumn(endPoint.Position, out int regionEndLine, out int _) != VSConstants.S_OK))
            {
                return false;
            }

            if (direction == DirectionUp)
            {
                if (regionStartColumn >= caretColumn)
                {
                    nextLine = regionStartLine;
                }
                else
                {
                    bool regionEndsAtEndOfLine = 
                        (view.GetLineAndColumn(endPoint.Position + 2, out int lineOfPositionAfterRegionEnd, out int _) == VSConstants.S_OK)
                        && (lineOfPositionAfterRegionEnd > regionEndLine);
                    nextLine = regionEndsAtEndOfLine ? regionEndLine : regionStartLine - 1;
                }
            }
            else // Down
            {
                bool regionStartsToTheRightOfCaret = caretLine == regionStartLine;
                nextLine = regionStartsToTheRightOfCaret ? regionEndLine + 1 : regionEndLine;
            }

            return true;
        }

        private static void UpdateSelection(SnapshotPoint previousPoint, SnapshotPoint currentPoint, bool extendSelection)
        {
            var selection = TextViewManager.Instance.ActiveTextView.Selection;
            if (!extendSelection)
            {
                selection.Clear();
                return;
            }

            SnapshotPoint newSelectionStart, newSelectionEnd;
            if (selection.IsEmpty)
            {
                newSelectionStart = newSelectionEnd = previousPoint;
            }
            else
            {
                newSelectionStart = selection.Start.Position;
                newSelectionEnd = selection.End.Position;
            }

            if (previousPoint == selection.Start.Position)
                newSelectionStart = currentPoint;
            else
                newSelectionEnd = currentPoint;

            if (newSelectionStart > newSelectionEnd)
            {
                var temp = newSelectionStart;
                newSelectionStart = newSelectionEnd;
                newSelectionEnd = temp;
            }

            selection.Select(new SnapshotSpan(newSelectionStart, newSelectionEnd), isReversed: currentPoint == newSelectionStart);
        }
    }
}
