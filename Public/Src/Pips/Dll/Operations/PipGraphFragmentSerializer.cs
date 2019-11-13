// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// Class to handle serialization/deserialization of pip graph fragments
    /// </summary>
    public class PipGraphFragmentSerializer
    {
        /// <summary>
        /// Total pips to deserialize in the fragment.  Until this is read in from the file, it is 0.
        /// </summary>
        public int TotalPipsToDeserialize => m_totalPipsToDeserialize;

        /// <summary>
        /// Total pips to deserialize in the fragment.  Until this is read in from the file, it is 0.
        /// </summary>
        public int TotalPipsToSerialize => m_totalPipsToSerialize;

        /// <summary>
        /// Total pips deserialized so far.
        /// </summary>
        public int PipsDeserialized => Stats.PipsDeserialized;

        /// <summary>
        /// Total pips serialized so far.
        /// </summary>
        public int PipsSerialized => Stats.PipsSerialized;

        /// <summary>
        /// Description of the fragment, for printing on the console
        /// </summary>
        public string FragmentDescription { get; private set; }

        /// <summary>
        /// The alternate symbol separator for use when serializing <see cref="FullSymbol"/>s.
        /// </summary>
        public char AlternateSymbolSeparator { get; set; }

        private readonly PipExecutionContext m_pipExecutionContext;
        private readonly PipGraphFragmentContext m_pipGraphFragmentContext;

        private volatile int m_totalPipsToDeserialize = 0;
        private volatile int m_totalPipsToSerialize = 0;

        /// <summary>
        /// Detailed statistics of serialization and deserialization.
        /// </summary>
        public readonly SerializeStats Stats = new SerializeStats();

        /// <summary>
        /// Creates an instance of <see cref="PipGraphFragmentSerializer"/>.
        /// </summary>
        public PipGraphFragmentSerializer(PipExecutionContext pipExecutionContext, PipGraphFragmentContext pipGraphFragmentContext)
        {
            Contract.Requires(pipExecutionContext != null);
            Contract.Requires(pipGraphFragmentContext != null);

            m_pipExecutionContext = pipExecutionContext;
            m_pipGraphFragmentContext = pipGraphFragmentContext;
        }

        /// <summary>
        /// Deserializes a pip graph fragment and call the given handleDeserializedPip function on each pip deserialized.
        /// </summary>
        public async Task<bool> DeserializeAsync(
            AbsolutePath filePath,
            Func<PipGraphFragmentContext, PipGraphFragmentProvenance, PipId, Pip, Task<bool>> handleDeserializedPip = null,
            string fragmentDescriptionOverride = null)
        {
            Contract.Requires(filePath.IsValid);
            string fileName = filePath.ToString(m_pipExecutionContext.PathTable);

            if (!File.Exists(fileName))
            {
                throw new FileNotFoundException($"File '{fileName}' not found");
            }

            using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return await DeserializeAsync(stream, handleDeserializedPip, fragmentDescriptionOverride, filePath);
            }
        }

        /// <summary>
        /// Deserializes a pip graph fragment from stream.
        /// </summary>
        public async Task<bool> DeserializeAsync(
            Stream stream,
            Func<PipGraphFragmentContext, PipGraphFragmentProvenance, PipId, Pip, Task<bool>> handleDeserializedPip = null,
            string fragmentDescriptionOverride = null,
            AbsolutePath filePathOrigin = default)
        {
            using (var reader = new PipRemapReader(m_pipExecutionContext, m_pipGraphFragmentContext, stream))
            {
                try
                {
                    string serializedDescription = reader.ReadNullableString();
                    FragmentDescription = fragmentDescriptionOverride ?? serializedDescription;
                    var provenance = new PipGraphFragmentProvenance(filePathOrigin, FragmentDescription);
                    bool serializedUsingTopSort = reader.ReadBoolean();
                    Func<PipId, Pip, Task<bool>> handleDeserializedPipInFragment = (pipId, pip) => handleDeserializedPip(m_pipGraphFragmentContext, provenance, pipId, pip);
                    if (serializedUsingTopSort)
                    {
                        return await DeserializeTopSortAsync(handleDeserializedPipInFragment, reader);
                    }
                    else
                    {
                        return await DeserializeSeriallyAsync(handleDeserializedPipInFragment, reader);
                    }
                }
                finally
                {
                    Interlocked.Add(ref Stats.OptimizedSymbols, reader.OptimizedSymbols);
                }
            }
        }

        private async Task<bool> DeserializeSeriallyAsync(Func<PipId, Pip, Task<bool>> handleDeserializedPip, PipRemapReader reader)
        {
            bool successful = true;
            m_totalPipsToDeserialize = reader.ReadInt32();
            for (int totalPipsRead = 0; totalPipsRead < m_totalPipsToDeserialize; totalPipsRead++)
            {
                var pip = Pip.Deserialize(reader);
                var pipId = new PipId(reader.ReadUInt32());

                if (!await handleDeserializedPip(pipId, pip))
                {
                    successful = false;
                }

                Stats.Increment(pip, serialize: false);
            }

            return successful;
        }

        private async Task<bool> DeserializeTopSortAsync(Func<PipId, Pip, Task<bool>> handleDeserializedPip, PipRemapReader reader)
        {
            bool successful = true;
            m_totalPipsToDeserialize = reader.ReadInt32();
            int totalPipsRead = 0;
            while (totalPipsRead < m_totalPipsToDeserialize)
            {
                var deserializedPips = reader.ReadReadOnlyList((deserializer) =>
                {
                    var pip = Pip.Deserialize(reader);

                    // Pip id is not deserialized when pip is deserialized.
                    // Pip id must be read separately. To be able to add a pip to the graph, the pip id of the pip
                    // is assumed to be unset, and is set when the pip gets inserted into the pip table.
                    // Thus, one should not assign the pip id of the deserialized pip with the deserialized pip id.
                    // Do not use reader.ReadPipId() for reading the deserialized pip id. The method reader.ReadPipId() 
                    // remaps the pip id to a new pip id.
                    var pipId = new PipId(reader.ReadUInt32());
                    return (pip, pipId);
                });

                totalPipsRead += deserializedPips.Count;
                Task<bool>[] tasks = new Task<bool>[deserializedPips.Count];

                for (int i = 0; i < deserializedPips.Count; i++)
                {
                    var (pip, pipId) = deserializedPips[i];
                    tasks[i] = HandleAndReportDeserializedPipAsync(handleDeserializedPip, pipId, pip);
                }

                successful &= (await Task.WhenAll(tasks)).All(x => x);
            }

            return successful;
        }

        private async Task<bool> HandleAndReportDeserializedPipAsync(Func<PipId, Pip, Task<bool>> handleDeserializedPip, PipId pipId, Pip pip)
        {
            var result = await handleDeserializedPip(pipId, pip);
            Stats.Increment(pip, serialize: false);
            return result;
        }

        /// <summary>
        /// Serializes pip graph fragment.
        /// </summary>
        public void Serialize(AbsolutePath filePath, IPipGraph pipGraph, string fragmentDescription = null, bool useTopSortSerialization = false)
        {
            if (useTopSortSerialization)
            {
                var topSorter = new PipGraphFragmentTopSort(pipGraph);
                var sortedPips = topSorter.Sort();
                int pipCount = sortedPips.Aggregate(0, (n, layer) => n + layer.Count);

                SerializeTopSort(filePath, sortedPips, pipCount, fragmentDescription);
            }
            else
            {
                SerializeSerially(filePath, pipGraph.RetrieveScheduledPips().ToList(), fragmentDescription);
            }
        }

        /// <summary>
        /// Serializes list of pips to a file.
        /// </summary>
        private void SerializeSerially(AbsolutePath filePath, IReadOnlyList<Pip> pipsToSerialize, string fragmentDescription = null)
        {
            string fileName = filePath.ToString(m_pipExecutionContext.PathTable);
            using (var stream = GetStream(fileName))
            {
                SerializeSerially(stream, pipsToSerialize, fragmentDescription ?? fileName);
            }
        }

        /// <summary>
        /// Serializes list of pips to a file.
        /// </summary>
        private void SerializeSerially(Stream stream, IReadOnlyList<Pip> pipsToSerialize, string fragmentDescription)
        {
            Contract.Requires(pipsToSerialize != null);
            using (var writer = GetRemapWriter(stream))
            {
                SerializeHeader(writer, fragmentDescription, false);
                m_totalPipsToSerialize = pipsToSerialize.Count;
                writer.Write(pipsToSerialize.Count);
                foreach (var pip in pipsToSerialize)
                {
                    pip.Serialize(writer);
                    writer.Write(pip.PipId.Value);
                    Stats.Increment(pip, serialize: true);
                }
            }
        }

        /// <summary>
        /// Serializes list of pips to a file using topological sorting so that each level can be added to the graph in parallel.
        /// </summary>
        public void SerializeTopSort(AbsolutePath filePath, IReadOnlyCollection<IReadOnlyList<Pip>> pipsToSerialize, int totalPipCount, string fragmentDescription = null)
        {
            string fileName = filePath.ToString(m_pipExecutionContext.PathTable);
            using (var stream = GetStream(fileName))
            {
                SerializeTopSort(stream, pipsToSerialize, totalPipCount, fragmentDescription ?? fileName);
            }
        }

        /// <summary>
        /// Serializes list of pips to a file using topological sorting so that each level can be added to the graph in parallel.
        /// </summary>
        public void SerializeTopSort(Stream stream, IReadOnlyCollection<IReadOnlyList<Pip>> pipsToSerialize, int totalPipCount, string fragmentDescription)
        {
            Contract.Requires(pipsToSerialize != null);
            using (var writer = GetRemapWriter(stream))
            {
                SerializeHeader(writer, fragmentDescription, true);
                m_totalPipsToSerialize = totalPipCount;
                writer.Write(totalPipCount);
                foreach (var pipGroup in pipsToSerialize)
                {
                    writer.WriteReadOnlyList(pipGroup, (serializer, pip) =>
                    {
                        pip.Serialize(writer);
                        writer.Write(pip.PipId.Value);
                        Stats.Increment(pip, serialize: true);
                    });
                }
            }
        }

        private FileStream GetStream(string fileName)
        {
            return new FileStream(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        private PipRemapWriter GetRemapWriter(Stream stream)
        {
            // Specify alternate symbol separator to reduce full strings in symbol table for value names which are split by the specified character
            return new PipRemapWriter(m_pipExecutionContext, m_pipGraphFragmentContext, stream, alternateSymbolSeparator: AlternateSymbolSeparator);
        }

        private void SerializeHeader(PipRemapWriter writer, string fragmentDescription, bool topSort)
        {
            writer.WriteNullableString(fragmentDescription);
            writer.Write(topSort);
        }

        /// <summary>
        /// Class tracking for statistics of serialization/deserialization.
        /// </summary>
        public class SerializeStats
        {
            private readonly int[] m_pips;
            private readonly int[] m_serviceKinds;
            private int m_serializedPipCount;
            private int m_deserializedPipCount;

            /// <summary>
            /// Number of serialized pips.
            /// </summary>
            public int PipsSerialized => Volatile.Read(ref m_serializedPipCount);

            /// <summary>
            /// Number of deserialized pips.
            /// </summary>
            public int PipsDeserialized => Volatile.Read(ref m_deserializedPipCount);

            /// <summary>
            /// The number of optimized symbols
            /// </summary>
            public int OptimizedSymbols;

            /// <summary>
            /// Creates an instance of <see cref="SerializeStats"/>.
            /// </summary>
            public SerializeStats()
            {
                m_pips = new int[(int)PipType.Max];
                m_serviceKinds = new int[(int)Enum.GetValues(typeof(ServicePipKind)).Cast<ServicePipKind>().Max() + 1];
            }

            /// <summary>
            /// Increments stats.
            /// </summary>
            public void Increment(Pip pip, bool serialize)
            {
                if (serialize)
                {
                    Interlocked.Increment(ref m_serializedPipCount);
                }
                else
                {
                    Interlocked.Increment(ref m_deserializedPipCount);
                }

                ++m_pips[(int)pip.PipType];

                if (pip.PipType == PipType.Process)
                {
                    Process process = pip as Process;
                    if (process.ServiceInfo != null && process.ServiceInfo != ServiceInfo.None)
                    {
                        ++m_serviceKinds[(int)process.ServiceInfo.Kind];
                    }
                }
            }

            /// <inheritdoc />
            public override string ToString()
            {
                var builder = new StringBuilder();
                builder.AppendLine();
                builder.AppendLine($"    Serialized pips: {PipsSerialized}");
                builder.AppendLine($"    Deserialized pips: {PipsDeserialized}");
                builder.AppendLine($"    Optimized symbols: {OptimizedSymbols}");
                for (int i = 0; i < m_pips.Length; ++i)
                {
                    PipType pipType = (PipType)i;
                    builder.AppendLine($"    {pipType.ToString()}: {m_pips[i]}");
                    if (pipType == PipType.Process)
                    {
                        for (int j = 0; j < m_serviceKinds.Length; ++j)
                        {
                            ServicePipKind servicePipKind = (ServicePipKind)j;
                            if (servicePipKind != ServicePipKind.None)
                            {
                                builder.AppendLine($"        {servicePipKind.ToString()}: {m_serviceKinds[j]}");
                            }
                        }
                    }
                }

                return builder.ToString();
            }
        }
    }
}