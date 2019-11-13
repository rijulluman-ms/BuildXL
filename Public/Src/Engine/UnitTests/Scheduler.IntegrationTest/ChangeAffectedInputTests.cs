// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using Test.BuildXL.Executables.TestProcess;
using Test.BuildXL.Scheduler;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTest.BuildXL.Scheduler
{
    public class ChangeAffectedInputTests : SchedulerIntegrationTestBase
    {
        public ChangeAffectedInputTests(ITestOutputHelper output) : base(output)
        {
            Environment.SetEnvironmentVariable("[Sdk.BuildXL]qCodeCoverageEnumType", "DynamicCodeCov");
        }

        [Fact]
        public void DirectAffectedFileInputTest()
        {
            // aInput->(pipA)->aOutput->(pipB)->bOutput
            // Expected change affected input for pipB is aOutput.

            // Process A.
            var dir = Path.Combine(ObjectRoot, "Dir");
            var dirPath = AbsolutePath.Create(Context.PathTable, dir);

            FileArtifact aInput = CreateSourceFile(root: dirPath, prefix: "pip-a-input-file");
            FileArtifact aOutput = CreateOutputFileArtifact(root: dirPath, prefix: "pip-a-out-file");
            var pipBuilderA= CreatePipBuilder(new[] { Operation.ReadFile(aInput), Operation.WriteFile(aOutput) });
            SchedulePipBuilder(pipBuilderA);

            // Process B.
            FileArtifact bOutput = CreateOutputFileArtifact(root: dirPath, prefix: "pip-b-out-file");
            AbsolutePath changeAffectedWrittenFile = CreateUniqueObjPath("change");
            var pipBuilderB = CreatePipBuilder(new[] 
            { 
                // Ensure that pip reads the changeAffectedWrittenFile if it exists.
                Operation.ReadFile(FileArtifact.CreateSourceFile(changeAffectedWrittenFile), doNotInfer: true), 
                Operation.ReadFile(aOutput), 
                Operation.WriteFile(bOutput) 
            });
            SchedulePipBuilder(pipBuilderB);
            RunScheduler().AssertSuccess();

            // reset the graph, so we can SetChangeAffectedInputListWrittenFile for pipB
            ResetPipGraphBuilder();
            var pipA = SchedulePipBuilder(pipBuilderA);
            pipBuilderB.SetChangeAffectedInputListWrittenFile(changeAffectedWrittenFile);
            var pipB = SchedulePipBuilder(pipBuilderB);

            var inputChangesFile = CreateOutputFileArtifact();
            File.WriteAllText(ArtifactToString(inputChangesFile), ArtifactToString(aInput));
            Configuration.Schedule.InputChanges = inputChangesFile.Path;

            // changeAffectedWrittenFile of pipB contains aOutput, expecting cache miss
            var result = RunScheduler();
            result.AssertCacheMiss(pipB.Process.PipId);
            result.AssertCacheHit(pipA.Process.PipId);

            var actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            var expectedAffectedInput = ArtifactToString(aOutput);
            XAssert.AreEqual(expectedAffectedInput, actualAffectedInput);
        }

        [Fact]
        public void RevertChangeCacheMissTest()
        {
            // aInput-->(pipA)-->aOutput-->(pipC)-->cOutput
            //                            /                         
            // bInput-->(pipB)-->bOutput--

            var dir = Path.Combine(ObjectRoot, "Dir");
            var dirPath = AbsolutePath.Create(Context.PathTable, dir);

            FileArtifact changeList = CreateSourceFile(root: dirPath, prefix: "changeList-file");

            // Process A.
            FileArtifact aInput = CreateSourceFile(root: dirPath, prefix: "pip-a-input-file");
            FileArtifact aOutput = CreateOutputFileArtifact(root: dirPath, prefix: "pip-a-out-file");

            var pipBuilderA = CreatePipBuilder(new[] { Operation.ReadFile(aInput), Operation.WriteFile(aOutput) });
            var pipA = SchedulePipBuilder(pipBuilderA);

            // Process B.
            FileArtifact bInput = CreateSourceFile(root: dirPath, prefix: "pip-b-input-file");
            FileArtifact bOutput = CreateOutputFileArtifact(root: dirPath, prefix: "pip-b-out-file");
            File.WriteAllText(ArtifactToString(bInput), "pipBOrigin");
            var pipBuilderB = CreatePipBuilder(new[] { Operation.ReadFile(bInput), Operation.WriteFile(bOutput) });
            var pipB = SchedulePipBuilder(pipBuilderB);

            // Process C.
            FileArtifact cOutput = CreateOutputFileArtifact(root: dirPath, prefix: "pip-c-out-file");
            AbsolutePath changeAffectedWrittenFile = CreateUniqueObjPath("change");
            var pipBuilderC = CreatePipBuilder(new[]
            { 
                // Ensure that pip reads the changeAffectedWrittenFile if it exists.
                Operation.ReadFile(FileArtifact.CreateSourceFile(changeAffectedWrittenFile), doNotInfer: true),
                Operation.ReadFile(aOutput),
                Operation.ReadFile(bOutput),
                Operation.WriteFile(cOutput)
            });
            pipBuilderC.SetChangeAffectedInputListWrittenFile(changeAffectedWrittenFile);
            var pipC = SchedulePipBuilder(pipBuilderC);
            Configuration.Schedule.InputChanges = changeList.Path;

            // Build0
            File.WriteAllText(ArtifactToString(changeList), "");
            var result = RunScheduler();
            result.AssertCacheMiss(pipA.Process.PipId, pipB.Process.PipId, pipC.Process.PipId);
            var actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            var expectedAffectedInput = "";
            XAssert.AreEqual(expectedAffectedInput, actualAffectedInput);

            // Build1 with change in aInput. 
            // Dependencies of pipA and pipB changed, so they get cache miss. 
            // m_changeAffectedInputListWrittenFile of pipC contains aOutput
            File.WriteAllText(ArtifactToString(aInput), "pipA");
            File.WriteAllText(ArtifactToString(changeList), ArtifactToString(aInput));
            result = RunScheduler();
            result.AssertCacheMiss(pipA.Process.PipId, pipC.Process.PipId);
            result.AssertCacheHit(pipB.Process.PipId);
            actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            expectedAffectedInput = ArtifactToString(aOutput);
            XAssert.AreEqual(expectedAffectedInput, actualAffectedInput);

            // Build2 with change in bInput. 
            // Dependencies of pipB and pipC changed, so they get cache miss.
            // m_changeAffectedInputListWrittenFile of pipC contains bOutput
            File.WriteAllText(ArtifactToString(bInput), "pipBChange");
            File.WriteAllText(ArtifactToString(changeList), ArtifactToString(bInput));

            result = RunScheduler();
            result.AssertCacheHit(pipA.Process.PipId);
            result.AssertCacheMiss(pipB.Process.PipId, pipC.Process.PipId);

            actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            expectedAffectedInput = ArtifactToString(bOutput);
            XAssert.AreEqual(expectedAffectedInput, actualAffectedInput);

            // Build3 reverts change in bInput. pipB get cache hit of build0.  
            // m_changeAffectedInputListWrittenFile of pipC contains bOutput, aOutput cache hit from build1, bOutput from build0. So pipC get cache miss.
            File.WriteAllText(ArtifactToString(bInput), "pipBOrigin");
            File.WriteAllText(ArtifactToString(changeList), ArtifactToString(bInput));

            result = RunScheduler();
            result.AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId);
            result.AssertCacheMiss(pipC.Process.PipId);

            actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            expectedAffectedInput = ArtifactToString(bOutput);
            XAssert.AreEqual(expectedAffectedInput, actualAffectedInput);
        }

        [Fact]
        public void DirectAffectedDiretoryInputTest()
        {
            // aInputDir -> (pipA) -> aOutputDir -> (PipB) -> bOutputDir
            //  |- pip-a-input-file   |- pip-a-out-file       |- pip-b-out-file           
            //                        |- aSubOutputDir
            //                         |- pip-a-out-in-sub-file
            // Expected change affected inputs for pipB is pip-a-out-file and pip-a-out-in-sub-file

            var aInputDir = Path.Combine(ObjectRoot, "input");
            var aInputDirPath = AbsolutePath.Create(Context.PathTable, aInputDir);
            var aInputDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aInputDirPath);
            var aInputFile = CreateSourceFile(root: aInputDirPath, prefix: "pip-a-input-file");
            File.WriteAllText(ArtifactToString(aInputFile), "pipABuild1");
            var sealInputDir = SealDirectory(aInputDirPath, SealDirectoryKind.SourceAllDirectories);


            var aOutputDir = Path.Combine(ObjectRoot, "aOutputDir");
            var aOutputDirPath = AbsolutePath.Create(Context.PathTable, aOutputDir);
            var aOutputDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aOutputDirPath);
            var aOutputFileInOutputeDir = CreateOutputFileArtifact(root: aOutputDir, prefix: "pip-a-out-file");
            var aOutputSubDir = Path.Combine(aOutputDir, "aSubOutputDir");
            var aOutputSubDirPath = AbsolutePath.Create(Context.PathTable, aOutputSubDir);
            var aOutputSubDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aOutputSubDirPath);
            var aOutputFileInOutputSubDir = CreateOutputFileArtifact(root: aOutputSubDir, prefix: "pip-a-out-in-sub-file");

            var bOutDir = Path.Combine(ObjectRoot, "bOutputDir");
            var bOutDirPath = AbsolutePath.Create(Context.PathTable, bOutDir);
            var bOutDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(bOutDirPath);
            var bOutFileArtifact = CreateOutputFileArtifact(root: bOutDirArtifact, prefix: "pip-b-out-file");

            var pipBuilderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(aInputFile, doNotInfer: true),
                Operation.WriteFile(aOutputFileInOutputeDir, doNotInfer: true),
                Operation.CreateDir(aOutputSubDirArtifact, doNotInfer:true),
                Operation.WriteFile(aOutputFileInOutputSubDir, doNotInfer: true),
            });
            pipBuilderA.AddInputDirectory(sealInputDir);
            pipBuilderA.AddOutputDirectory(aOutputDirArtifact, SealDirectoryKind.Opaque);
            var pipA = SchedulePipBuilder(pipBuilderA);

            var changeAffectedWrittenFile = CreateUniqueObjPath("change");
            var pipBuilderB = CreatePipBuilder(new Operation[]
            {
                // Ensure that pip reads the changeAffectedWrittenFile if it exists.
                Operation.ReadFile(FileArtifact.CreateSourceFile(changeAffectedWrittenFile), doNotInfer: true),
                Operation.ReadFile(aOutputFileInOutputSubDir, doNotInfer: true),
                Operation.WriteFile(bOutFileArtifact, doNotInfer: true),
            });
            pipBuilderB.AddInputDirectory(pipA.ProcessOutputs.GetOpaqueDirectory(aOutputDirPath));
            pipBuilderB.AddOutputDirectory(bOutDirArtifact, SealDirectoryKind.Opaque);
            pipBuilderB.SetChangeAffectedInputListWrittenFile(changeAffectedWrittenFile);
            var pipB = SchedulePipBuilder(pipBuilderB);

            var inputChangesFile = CreateOutputFileArtifact();
            File.WriteAllText(ArtifactToString(inputChangesFile), ArtifactToString(aInputFile));
            Configuration.Schedule.InputChanges = inputChangesFile.Path;

            RunScheduler().AssertSuccess();

            string[] actualAffectedSortedInputs = File.ReadAllLines(changeAffectedWrittenFile.ToString(Context.PathTable)).OrderBy(p => p, StringComparer.InvariantCultureIgnoreCase).ToArray();
            string[] expectedAffectedInputs = 
            {
                ArtifactToString(aOutputFileInOutputSubDir),
                ArtifactToString(aOutputFileInOutputeDir)
            };

            string[] expectedAffectedSortedInputs = expectedAffectedInputs.OrderBy(p => p, StringComparer.InvariantCultureIgnoreCase).ToArray();
            XAssert.AreArraysEqual(expectedAffectedSortedInputs, actualAffectedSortedInputs, true);
        }

        public enum InputAccessType
        {
            DynamicFileAccess,
            DirectoryInput,
        }

        [Theory]
        [InlineData(InputAccessType.DynamicFileAccess)]
        [InlineData(InputAccessType.DirectoryInput)]
        public void TransitiveAffectedDirectoryInputTest(InputAccessType pipBInputAccessType)
        {
            // aInputDir -> (pipA) -> aOutputDir -> (pipB) -> bOutputDir -> (pipC) -> pip-c-out-file           
            //  |- pip-a-input-file    |- pip-a-out-file       |- pip-b-out-file   
            //                         |- aSubOutputDir
            //                            |- pip-a-out-in-sub-file
            // Expected change affected input for pipC is pip-b-out-file

            // Creating an input dir for pipA
            var aInputDir = Path.Combine(ObjectRoot, "input");
            var aInputDirPath = AbsolutePath.Create(Context.PathTable, aInputDir);
            var aInputDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aInputDirPath);
            var aInputFile = CreateSourceFile(root: aInputDirPath, prefix: "pip-a-input-file");
            File.WriteAllText(ArtifactToString(aInputFile), "pipABuild1");
            var sealInputDir = SealDirectory(aInputDirPath, SealDirectoryKind.SourceAllDirectories);

            // Creating an output dir Artifact (containing a outfile and a sub output dir) for pipB
            // This output dir will be input dir for pipB
            var aOutputDir = Path.Combine(ObjectRoot, "aOutputDir");
            var aOutputDirPath = AbsolutePath.Create(Context.PathTable, aOutputDir);
            var aOutputDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aOutputDirPath);
            var aOutputFileInOutputeDir = CreateOutputFileArtifact(root: aOutputDir, prefix: "pip-a-out-file");

            var aOutputSubDir = Path.Combine(aOutputDir, "aSubOutputDir");
            var aOutputSubDirPath = AbsolutePath.Create(Context.PathTable, aOutputSubDir);
            var aOutputSubDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aOutputSubDirPath);
            var aOutputFileInOutputSubDir = CreateOutputFileArtifact(root: aOutputSubDir, prefix: "pip-a-out-in-sub-file");

            // Creating an output dir for pipB, this will be the input dir for pipC
            var bOutDir = Path.Combine(ObjectRoot, "bOutputDir");
            var bOutDirPath = AbsolutePath.Create(Context.PathTable, bOutDir);
            var bOutDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(bOutDirPath);
            var bOutFileArtifact = CreateOutputFileArtifact(root: bOutDirArtifact, prefix: "pip-b-out-file");

            // pipC output a file
            var cOutFileArtifact = CreateOutputFileArtifact(prefix: "pip-c-out-file");
            var expectedAffectedInput = "";

            var pipBuilderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(aInputFile, doNotInfer: true),
                Operation.WriteFile(aOutputFileInOutputeDir, doNotInfer: true),
                Operation.CreateDir(aOutputSubDirArtifact, doNotInfer:true),
                Operation.WriteFile(aOutputFileInOutputSubDir, doNotInfer: true),
            });
            pipBuilderA.AddInputDirectory(sealInputDir);
            pipBuilderA.AddOutputDirectory(aOutputDirArtifact, SealDirectoryKind.Opaque); 
            var pipA = SchedulePipBuilder(pipBuilderA);

            var operations = new List<Operation>() { };
            if (pipBInputAccessType == InputAccessType.DynamicFileAccess)
            {
                operations.Add(Operation.ReadFile(aOutputFileInOutputeDir, doNotInfer: true));
                expectedAffectedInput = ArtifactToString(bOutFileArtifact);
            }
            operations.Add(Operation.WriteFile(bOutFileArtifact, doNotInfer: true));
            var pipBuilderB = CreatePipBuilder(operations);
            pipBuilderB.AddInputDirectory(pipA.ProcessOutputs.GetOpaqueDirectory(aOutputDirPath));
            pipBuilderB.AddOutputDirectory(bOutDirArtifact, SealDirectoryKind.Opaque);            
            var pipB = SchedulePipBuilder(pipBuilderB);

            var changeAffectedWrittenFile = CreateUniqueObjPath("change");
            var pipBuilderC = CreatePipBuilder(new Operation[]
            {
                // Ensure that pip reads the changeAffectedWrittenFile if it exists.
                Operation.ReadFile(FileArtifact.CreateSourceFile(changeAffectedWrittenFile), doNotInfer: true),
                Operation.ReadFile(bOutFileArtifact, doNotInfer: true),
                Operation.WriteFile(cOutFileArtifact),
            });
            pipBuilderC.AddInputDirectory(bOutDirArtifact);
            pipBuilderC.SetChangeAffectedInputListWrittenFile(changeAffectedWrittenFile);
            var pipC = SchedulePipBuilder(pipBuilderC);

            var inputChangesFile = CreateOutputFileArtifact();
            File.WriteAllText(ArtifactToString(inputChangesFile), ArtifactToString(aInputFile));
            Configuration.Schedule.InputChanges = inputChangesFile.Path;

            RunScheduler().AssertSuccess();
              
            var actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            XAssert.AreEqual(expectedAffectedInput, actualAffectedInput);  
        }

        [Theory]
        [InlineData(InputAccessType.DynamicFileAccess, true)]
        [InlineData(InputAccessType.DynamicFileAccess, false)]
        [InlineData(InputAccessType.DirectoryInput)]
        public void TransitiveAffectedDirectoryInputWithSealTest(InputAccessType pipBInputAccessType, bool accessExistingFile = false)
        {
            // aInputDir -> (pipA) ->pip-a-out-file-> (copy) ->pip-a-output-file-copy -> (seal) -> copyDir -> (pipB)     ->   bOutputDir -> (pipC) -> pip-c-out-file           
            //  |- pip-a-input-file                                                 /               |- pip-a-output-file-copy  |- pip-b-out-file   
            //                                                         existing-file                |- existing-file
            //                            
            // Expected change affected input for pipC is pip-b-out-file or ""    

            // Creating an output dir Artifact (containing a outfile and a sub output dir) for pipB
            // This output dir will be input dir for pipB
            var aInputDir = Path.Combine(ObjectRoot, "input");
            var aInputDirPath = AbsolutePath.Create(Context.PathTable, aInputDir);
            var aInputDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aInputDirPath);
            var aInputFile = CreateSourceFile(root: aInputDirPath, prefix: "pip-a-input-file");
            File.WriteAllText(ArtifactToString(aInputFile), "pipABuild1");
            var sealInputDir = SealDirectory(aInputDirPath, SealDirectoryKind.SourceAllDirectories);

            var aOutputDir = Path.Combine(ObjectRoot, "aOutputDir");
            var aOutputDirPath = AbsolutePath.Create(Context.PathTable, aOutputDir);
            var aOutputDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(aOutputDirPath);
            var aOutputFileInOutputeDir = CreateOutputFileArtifact(root: aOutputDir, prefix: "pip-a-out-file");

            var copyDir = Path.Combine(ObjectRoot, "copyDir");
            var copyDirPath = AbsolutePath.Create(Context.PathTable, copyDir);
            var copyFilePath = CreateSourceFile(root: copyDirPath, prefix: "pip-a-output-file-copy").Path;

            var aExistingFileInOutputeDir = CreateSourceFile(root: copyDirPath, prefix: "existing-file");
            File.WriteAllText(ArtifactToString(aExistingFileInOutputeDir), "This is an existing file");

            var bOutDir = Path.Combine(ObjectRoot, "bOutputDir");
            var bOutDirPath = AbsolutePath.Create(Context.PathTable, bOutDir);
            var bOutDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(bOutDirPath);
            var bOutFileArtifact = CreateOutputFileArtifact(root: bOutDirArtifact, prefix: "pip-b-out-file");

            var cOutFileArtifact = CreateOutputFileArtifact(prefix: "pip-c-out-file");

            var expectedAffectedInput = "";

            // pipA
            var pipBuilderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(aInputFile, doNotInfer: true),
                Operation.WriteFile(aOutputFileInOutputeDir),
            });
            pipBuilderA.AddInputDirectory(sealInputDir);
            var pipA = SchedulePipBuilder(pipBuilderA);

            // copy pip
            var copiedFile = CopyFile(aOutputFileInOutputeDir, copyFilePath);

            // seal dir pip
            var sealedaOutput = SealDirectory(copyDirPath, SealDirectoryKind.Full, aExistingFileInOutputeDir, copiedFile);


            // pipB
            var operations = new List<Operation>() { };
            if (pipBInputAccessType == InputAccessType.DynamicFileAccess)
            {
                if (accessExistingFile)
                {
                    operations.Add(Operation.ReadFile(aExistingFileInOutputeDir, doNotInfer: true));
                }
                else
                {
                    operations.Add(Operation.ReadFile(copiedFile, doNotInfer: true));
                    expectedAffectedInput = ArtifactToString(bOutFileArtifact);
                }
            }
            operations.Add(Operation.WriteFile(bOutFileArtifact, doNotInfer: true));
            var pipBuilderB = CreatePipBuilder(operations);
            pipBuilderB.AddInputDirectory(sealedaOutput);
            pipBuilderB.AddOutputDirectory(bOutDirArtifact, SealDirectoryKind.Opaque);
            var pipB = SchedulePipBuilder(pipBuilderB);


            // pipC
            var changeAffectedWrittenFile = CreateUniqueObjPath("change");
            var pipBuilderC = CreatePipBuilder(new Operation[]
            {
                // Ensure that pip reads the changeAffectedWrittenFile if it exists.
                Operation.ReadFile(FileArtifact.CreateSourceFile(changeAffectedWrittenFile), doNotInfer: true),
                Operation.ReadFile(bOutFileArtifact, doNotInfer: true),
                Operation.WriteFile(cOutFileArtifact),
            });
            pipBuilderC.AddInputDirectory(bOutDirArtifact);
            pipBuilderC.SetChangeAffectedInputListWrittenFile(changeAffectedWrittenFile);
            var pipC = SchedulePipBuilder(pipBuilderC);

            var inputChangesFile = CreateOutputFileArtifact();
            File.WriteAllText(ArtifactToString(inputChangesFile), ArtifactToString(aInputFile));
            Configuration.Schedule.InputChanges = inputChangesFile.Path;

            RunScheduler().AssertSuccess();

            var actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            XAssert.AreEqual(expectedAffectedInput, actualAffectedInput);
        }

        [Fact]
        public void AffectedSourceInsideSourceSealDirectory()
        {
            var sourceDirectory = CreateUniqueDirectory(SourceRootPath);
            var fileInsideSourceDirectory = CreateSourceFile(sourceDirectory, "file_");
            var sourceSealDirectory = CreateSourceSealDirectory(sourceDirectory, SealDirectoryKind.SourceTopDirectoryOnly, "file_*");
            PipGraphBuilder.AddSealDirectory(sourceSealDirectory);

            var changeAffectedWrittenFile = CreateUniqueObjPath("change");
            var pipBuilder = CreatePipBuilder(new Operation[]
            {
                // Ensure that pip reads the changeAffectedWrittenFile if it exists.
                Operation.ReadFile(FileArtifact.CreateSourceFile(changeAffectedWrittenFile), doNotInfer: true),
                Operation.ReadFile(fileInsideSourceDirectory, doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact()),
            });
            pipBuilder.AddInputDirectory(sourceSealDirectory.Directory);
            pipBuilder.SetChangeAffectedInputListWrittenFile(changeAffectedWrittenFile);
            SchedulePipBuilder(pipBuilder);
            var inputChangesFile = CreateOutputFileArtifact();
            File.WriteAllText(ArtifactToString(inputChangesFile), ArtifactToString(fileInsideSourceDirectory));
            Configuration.Schedule.InputChanges = inputChangesFile;

            RunScheduler().AssertSuccess();

            var actualAffectedInput = File.ReadAllText(changeAffectedWrittenFile.ToString(Context.PathTable));
            XAssert.AreEqual(ArtifactToString(fileInsideSourceDirectory), actualAffectedInput);
        }
    }
}
