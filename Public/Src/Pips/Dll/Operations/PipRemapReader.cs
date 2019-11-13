﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using BuildXL.Utilities;
using BuildXL.Utilities.Serialization;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// Reads in pips written with RemapWriter and remaps absolute paths, string ids from the value present in the stream to the value in the given for the same path/string in the given context.
    /// </summary>
    internal class PipRemapReader : PipReader
    {
        private readonly PipGraphFragmentContext m_pipGraphFragmentContext;
        private readonly PipExecutionContext m_pipExecutionContext;
        private readonly InliningReader m_inliningReader;
        private readonly PipDataEntriesPointerInlineReader m_pipDataEntriesPointerInlineReader;

        /// <summary>
        /// Gets the number of deserialized full symbols which were split by the a separator 
        /// </summary>
        public int OptimizedSymbols { get; private set; }

        /// <summary>
        /// Create a new RemapReader
        /// </summary>
        public PipRemapReader(PipExecutionContext pipExecutionContext, PipGraphFragmentContext pipGraphFragmentContext, Stream stream, bool debug = false, bool leaveOpen = true)
            : base(debug, pipExecutionContext.StringTable, stream, leaveOpen)
        {
            Contract.Requires(pipExecutionContext != null);
            Contract.Requires(pipGraphFragmentContext != null);
            Contract.Requires(stream != null);

            m_pipExecutionContext = pipExecutionContext;
            m_pipGraphFragmentContext = pipGraphFragmentContext;
            m_inliningReader = new InliningReader(stream, pipExecutionContext.PathTable, debug, leaveOpen);
            m_pipDataEntriesPointerInlineReader = new PipDataEntriesPointerInlineReader(this, stream, pipExecutionContext.PathTable, debug, leaveOpen);
        }

        /// <inheritdoc />
        public override PipId RemapPipId(PipId pipId) => m_pipGraphFragmentContext.RemapPipId(pipId);

        /// <inheritdoc />
        public override PipId ReadPipId() => RemapPipId(base.ReadPipId());

        /// <inheritdoc />
        public override DirectoryArtifact ReadDirectoryArtifact() => m_pipGraphFragmentContext.RemapDirectory(base.ReadDirectoryArtifact());

        /// <inheritdoc />
        public override AbsolutePath ReadAbsolutePath() => m_inliningReader.ReadAbsolutePath();

        /// <inheritdoc />
        public override StringId ReadStringId() => m_inliningReader.ReadStringId();

        /// <inheritdoc />
        public override PathAtom ReadPathAtom() => m_inliningReader.ReadPathAtom();

        /// <inheritdoc />
        public override FullSymbol ReadFullSymbol()
        {
            var alternateSymbolSeparator = ReadChar();
            if (alternateSymbolSeparator == default)
            {
                return FullSymbol.Create(m_pipExecutionContext.SymbolTable, ReadString());
            }
            else
            {
                OptimizedSymbols++;
                var segments = ReadArray(r => r.ReadStringId());
                var id = m_pipExecutionContext.SymbolTable.AddComponents(HierarchicalNameId.Invalid, segments);
                return new FullSymbol(id, alternateSymbolSeparator);
            }
        }

        /// <inheritdoc />
        public override StringId ReadPipDataEntriesPointer() => m_pipDataEntriesPointerInlineReader.ReadStringId();

        private class PipDataEntriesPointerInlineReader : InliningReader
        {
            private byte[] m_pipDatabuffer = new byte[1024];
            private readonly PipRemapReader m_baseInliningReader;

            public PipDataEntriesPointerInlineReader(PipRemapReader baseInliningReader, Stream stream, PathTable pathTable, bool debug = false, bool leaveOpen = true)
                : base(stream, pathTable, debug, leaveOpen)
            {
                m_baseInliningReader = baseInliningReader;
            }

            protected override BinaryStringSegment ReadBinaryStringSegment(ref byte[] buffer)
            {
                var (count, entries) = PipDataEntryList.Deserialize(m_baseInliningReader);

                return PipDataBuilder.WriteEntries(entries, count, ref m_pipDatabuffer);
            }
        }
    }
}
