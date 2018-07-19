﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;
using VSCommanding = Microsoft.VisualStudio.Commanding;

namespace Microsoft.CodeAnalysis.Editor.Implementation.Formatting
{
    [Export]
    [Export(typeof(VSCommanding.ICommandHandler))]
    [ContentType(ContentTypeNames.RoslynContentType)]
    [Name(PredefinedCommandHandlerNames.FormatDocument)]
    [Order(After = PredefinedCommandHandlerNames.Rename)]
    [Order(Before = PredefinedCommandHandlerNames.Completion)]
    internal partial class FormatCommandHandler :
        VSCommanding.ICommandHandler<FormatDocumentCommandArgs>,
        VSCommanding.ICommandHandler<FormatSelectionCommandArgs>,
        IChainedCommandHandler<PasteCommandArgs>,
        IChainedCommandHandler<TypeCharCommandArgs>,
        IChainedCommandHandler<ReturnKeyCommandArgs>
    {
        private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
        private readonly IEditorOperationsFactoryService _editorOperationsFactoryService;
        private readonly IWaitIndicator _waitIndicator;

        public string DisplayName => EditorFeaturesResources.Automatic_Formatting;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public FormatCommandHandler(
            ITextUndoHistoryRegistry undoHistoryRegistry,
            IEditorOperationsFactoryService editorOperationsFactoryService,
            IWaitIndicator waitIndicator)
        {
            _undoHistoryRegistry = undoHistoryRegistry;
            _editorOperationsFactoryService = editorOperationsFactoryService;
            _waitIndicator = waitIndicator;
        }

        private void Format(ITextView textView, Document document, TextSpan? selectionOpt, CancellationToken cancellationToken)
        {
            var formattingService = document.GetLanguageService<IEditorFormattingService>();

            using (Logger.LogBlock(FunctionId.CommandHandler_FormatCommand, KeyValueLogMessage.Create(LogType.UserAction, m => m["Span"] = selectionOpt?.Length ?? -1), cancellationToken))
            using (var transaction = CreateEditTransaction(textView, EditorFeaturesResources.Formatting))
            {
                var changes = formattingService.GetFormattingChangesAsync(document, selectionOpt, cancellationToken).WaitAndGetResult(cancellationToken);
                if (changes.Count == 0)
                {
                    return;
                }

                ApplyChanges(document, changes, selectionOpt, cancellationToken);
                transaction.Complete();
            }
        }

        private void ApplyChanges(Document document, IList<TextChange> changes, TextSpan? selectionOpt, CancellationToken cancellationToken)
        {
            AssertIsForeground();

            if (selectionOpt.HasValue)
            {
                var ruleFactory = document.Project.Solution.Workspace.Services.GetService<IHostDependentFormattingRuleFactoryService>();

                changes = ruleFactory.FilterFormattedChanges(document, selectionOpt.Value, changes).ToList();
                if (changes.Count == 0)
                {
                    return;
                }
            }

            using (Logger.LogBlock(FunctionId.Formatting_ApplyResultToBuffer, cancellationToken))
            {
                document.Project.Solution.Workspace.ApplyTextChanges(document.Id, changes, cancellationToken);
            }
        }

        private static bool CanExecuteCommand(ITextBuffer buffer)
        {
            return buffer.CanApplyChangeDocumentToWorkspace();
        }

        private static VSCommanding.CommandState GetCommandState(ITextBuffer buffer, Func<VSCommanding.CommandState> nextHandler)
        {
            if (!CanExecuteCommand(buffer))
            {
                return nextHandler();
            }

            return VSCommanding.CommandState.Available;
        }

        private static VSCommanding.CommandState GetCommandState(ITextBuffer buffer)
        {
            return CanExecuteCommand(buffer) ? VSCommanding.CommandState.Available : VSCommanding.CommandState.Unspecified;
        }

        public void ExecuteReturnOrTypeCommand(EditorCommandArgs args, Action nextHandler, CancellationToken cancellationToken)
        {
            // run next handler first so that editor has chance to put the return into the buffer first.
            nextHandler();

            var textView = args.TextView;
            var subjectBuffer = args.SubjectBuffer;
            if (!CanExecuteCommand(subjectBuffer))
            {
                return;
            }

            var caretPosition = textView.GetCaretPoint(args.SubjectBuffer);
            if (!caretPosition.HasValue)
            {
                return;
            }

            var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                return;
            }

            var service = document.GetLanguageService<IEditorFormattingService>();
            if (service == null)
            {
                return;
            }

            IList<TextChange> textChanges;

            // save current caret position
            if (args is ReturnKeyCommandArgs)
            {
                if (!service.SupportsFormatOnReturn)
                {
                    return;
                }

                textChanges = service.GetFormattingChangesOnReturnAsync(document, caretPosition.Value, cancellationToken).WaitAndGetResult(cancellationToken);
            }
            else if (args is TypeCharCommandArgs typeCharArgs)
            {
                if (!service.SupportsFormattingOnTypedCharacter(document, typeCharArgs.TypedChar))
                {
                    return;
                }

                textChanges = service.GetFormattingChangesAsync(document, typeCharArgs.TypedChar, caretPosition.Value, cancellationToken).WaitAndGetResult(cancellationToken);
            }
            else
            {
                throw ExceptionUtilities.UnexpectedValue(args);
            }

            if (textChanges == null || textChanges.Count == 0)
            {
                return;
            }

            using (var transaction = CreateEditTransaction(textView, EditorFeaturesResources.Automatic_Formatting))
            {
                transaction.MergePolicy = AutomaticCodeChangeMergePolicy.Instance;
                document.Project.Solution.Workspace.ApplyTextChanges(document.Id, textChanges, cancellationToken);
                transaction.Complete();
            }

            // get new caret position after formatting
            var newCaretPositionMarker = args.TextView.GetCaretPoint(args.SubjectBuffer);
            if (!newCaretPositionMarker.HasValue)
            {
                return;
            }

            var snapshotAfterFormatting = args.SubjectBuffer.CurrentSnapshot;

            var oldCaretPosition = caretPosition.Value.TranslateTo(snapshotAfterFormatting, PointTrackingMode.Negative);
            var newCaretPosition = newCaretPositionMarker.Value.TranslateTo(snapshotAfterFormatting, PointTrackingMode.Negative);
            if (oldCaretPosition.Position == newCaretPosition.Position)
            {
                return;
            }

            // caret has moved to wrong position, move it back to correct position
            args.TextView.TryMoveCaretToAndEnsureVisible(oldCaretPosition);
        }

        private CaretPreservingEditTransaction CreateEditTransaction(ITextView view, string description)
        {
            return new CaretPreservingEditTransaction(description, view, _undoHistoryRegistry, _editorOperationsFactoryService);
        }
    }
}
