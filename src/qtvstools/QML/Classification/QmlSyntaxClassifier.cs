﻿/****************************************************************************
**
** Copyright (C) 2018 The Qt Company Ltd.
** Contact: https://www.qt.io/licensing/
**
** This file is part of the Qt VS Tools.
**
** $QT_BEGIN_LICENSE:GPL-EXCEPT$
** Commercial License Usage
** Licensees holding valid commercial Qt licenses may use this file in
** accordance with the commercial license agreement provided with the
** Software or, alternatively, in accordance with the terms contained in
** a written agreement between you and The Qt Company. For licensing terms
** and conditions see https://www.qt.io/terms-conditions. For further
** information use the contact form at https://www.qt.io/contact-us.
**
** GNU General Public License Usage
** Alternatively, this file may be used under the terms of the GNU
** General Public License version 3 as published by the Free Software
** Foundation with exceptions as appearing in the file LICENSE.GPL3-EXCEPT
** included in the packaging of this file. Please review the following
** information to ensure the GNU General Public License requirements will
** be met: https://www.gnu.org/licenses/gpl-3.0.html.
**
** $QT_END_LICENSE$
**
****************************************************************************/

/// This file implements the actual highlighting of the text according to the
/// classification of the syntax elements recognized by the QML parser.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace QtVsTools.Qml.Classification
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("qml")]
    [TagType(typeof(ClassificationTag))]
    internal sealed class QmlSyntaxClassifierProvider : IViewTaggerProvider
    {
        [Export]
        [Name("qml")]
        [BaseDefinition("code")]
        internal static ContentTypeDefinition qmlContentType = null;

        [Export]
        [FileExtension(".qml")]
        [ContentType("qml")]
        internal static FileExtensionToContentTypeDefinition qmlFileType = null;

        [Export]
        [FileExtension(".qmlproject")]
        [ContentType("qml")]
        internal static FileExtensionToContentTypeDefinition qmlprojectFileType = null;

        [Import]
        internal IClassificationTypeRegistryService classificationTypeRegistry = null;

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            return new QmlSyntaxClassifier(buffer, classificationTypeRegistry) as ITagger<T>;
        }
    }

    internal sealed class QmlSyntaxClassifier : ITagger<ClassificationTag>
    {
        ITextBuffer buffer;
        Dispatcher dispatcher;
        DispatcherTimer timer;

        internal QmlSyntaxClassifier(ITextBuffer buffer,
                               IClassificationTypeRegistryService typeService)
        {
            this.buffer = buffer;
            QmlClassificationType.InitClassificationTypes(typeService);
            ParseQML(buffer.CurrentSnapshot);
            buffer.Changed += Buffer_Changed;

            dispatcher = Dispatcher.CurrentDispatcher;
            timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timer.Tick += Timer_Tick;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            timer.Stop();
            var snapshot = buffer.CurrentSnapshot;
            AsyncParseQML(snapshot);
        }

        private void Buffer_Changed(object sender, TextContentChangedEventArgs e)
        {
            AsyncParseQML(e.After);
            timer.Stop();
            timer.Start();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        bool flag = false;
        List<QmlTag> tags = new List<QmlTag>();
        object syncChanged = new object();

        async void AsyncParseQML(ITextSnapshot snapshot)
        {
            if (flag)
                return;
            flag = true;
            await Task.Run(() =>
            {
                ParseQML(snapshot);
                flag = false;
                var currentVersion = buffer.CurrentSnapshot.Version;
                if (snapshot.Version.VersionNumber == currentVersion.VersionNumber) {
                    timer.Stop();
                } else {
                    timer.Start();
                }
            });
        }

        void ParseQML(ITextSnapshot snapshot)
        {
            bool parsedCorrectly = true;
            lock (syncChanged) {
                tags.Clear();
                var text = snapshot.GetText();
                using (var parser = Parser.Parse(text)) {
                    parsedCorrectly = parser.ParsedCorrectly;
                    foreach (var token in parser.Tokens) {
                        if (parsedCorrectly
                            || token.Location.Offset < parser.FirstErrorOffset) {
                            tags.AddRange(QmlTag.GetClassification(snapshot, token));
                        }
                    }
                    foreach (var node in parser.AstNodes) {
                        if (parsedCorrectly
                            || node.FirstSourceLocation.Offset < parser.FirstErrorOffset) {
                            tags.AddRange(QmlTag.GetClassification(snapshot, node));
                        }
                    }
                }
            }
            var tagsChangedHandler = TagsChanged;
            if (parsedCorrectly) {
                var span = new SnapshotSpan(buffer.CurrentSnapshot,
                    0, buffer.CurrentSnapshot.Length);
                if (tagsChangedHandler != null)
                    tagsChangedHandler.Invoke(this, new SnapshotSpanEventArgs(span));
            } else {
                List<QmlTag> tagsCopy;
                lock (syncChanged) {
                    tagsCopy = new List<QmlTag>(tags);
                }
                foreach (var tag in tagsCopy) {
                    var tagSpan = tag.ToTagSpan(snapshot);
                    if (tagsChangedHandler != null)
                        tagsChangedHandler.Invoke(this, new SnapshotSpanEventArgs(tagSpan.Span));
                }
            }
        }

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            List<QmlTag> tagsCopy;
            var snapshot = spans[0].Snapshot;
            lock (syncChanged) {
                tagsCopy = new List<QmlTag>(tags);
            }
            foreach (var tag in tagsCopy) {
                var tagSpan = tag.ToTagSpan(snapshot);
                if (tagSpan.Span.Length == 0)
                    continue;

                if (!spans.IntersectsWith(new NormalizedSnapshotSpanCollection(tagSpan.Span)))
                    continue;

                if (tag.ClassificationType == null)
                    continue;

                yield return
                        new TagSpan<ClassificationTag>(tagSpan.Tag.Span.GetSpan(snapshot),
                        new ClassificationTag(tag.ClassificationType));
            }
        }
    }
}
