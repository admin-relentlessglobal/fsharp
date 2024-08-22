// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// The configuration of the compiler (TcConfig and TcConfigBuilder)
module internal FSharp.Compiler.CompilerConfig

open System
open FSharp.Compiler.IO
open Internal.Utilities
open Internal.Utilities.Library
open FSharp.Compiler
open FSharp.Compiler.Xml
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryReader
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.DependencyManager
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Features
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.BuildGraph

exception FileNameNotResolved of searchedLocations: string * fileName: string * range: range

exception LoadedSourceNotFoundIgnoring of fileName: string * range: range

/// Represents a reference to an F# assembly. May be backed by a real assembly on disk (read by Abstract IL), or a cross-project
/// reference in FSharp.Compiler.Service.
type IRawFSharpAssemblyData =

    /// The raw list AutoOpenAttribute attributes in the assembly
    abstract GetAutoOpenAttributes: unit -> string list

    /// The raw list InternalsVisibleToAttribute attributes in the assembly
    abstract GetInternalsVisibleToAttributes: unit -> string list

    /// The raw IL module definition in the assembly, if any. This is not present for cross-project references
    /// in the language service
    abstract TryGetILModuleDef: unit -> ILModuleDef option

    /// Indicates if the assembly has any F# signature data attribute
    abstract HasAnyFSharpSignatureDataAttribute: bool

    /// Indicates if the assembly has an F# signature data attribute suitable for use with this version of F# tooling
    abstract HasMatchingFSharpSignatureDataAttribute: bool

    /// Get the raw F# signature data in the assembly, if any
    abstract GetRawFSharpSignatureData:
        m: range * ilShortAssemName: string * fileName: string ->
            (string * ((unit -> ReadOnlyByteMemory) * (unit -> ReadOnlyByteMemory) option)) list

    /// Get the raw F# optimization data in the assembly, if any
    abstract GetRawFSharpOptimizationData:
        m: range * ilShortAssemName: string * fileName: string ->
            (string * ((unit -> ReadOnlyByteMemory) * (unit -> ReadOnlyByteMemory) option)) list

    /// Get the table of type forwarders in the assembly
    abstract GetRawTypeForwarders: unit -> ILExportedTypesAndForwarders

    /// Get the identity of the assembly
    abstract ILScopeRef: ILScopeRef

    /// Get the identities of the assemblies referenced by this assembly
    abstract ILAssemblyRefs: ILAssemblyRef list

    /// Get the short name for this assembly
    abstract ShortAssemblyName: string

type TimeStampCache =

    new: defaultTimeStamp: DateTime -> TimeStampCache

    member GetFileTimeStamp: fileName: string -> DateTime

    member GetProjectReferenceTimeStamp: projectReference: IProjectReference -> DateTime

and [<RequireQualifiedAccess>] ProjectAssemblyDataResult =

    | Available of IRawFSharpAssemblyData

    | Unavailable of useOnDiskInstead: bool

and IProjectReference =

    /// The name of the assembly file generated by the project
    abstract FileName: string

    /// Evaluate raw contents of the assembly file generated by the project.
    /// 'None' may be returned if an in-memory view of the contents is, for some reason,
    /// not available.  In this case the on-disk view of the contents will be preferred.
    abstract EvaluateRawContents: unit -> Async<ProjectAssemblyDataResult>

    /// Get the logical timestamp that would be the timestamp of the assembly file generated by the project.
    ///
    /// For project references this is maximum of the timestamps of all dependent files.
    /// The project is not actually built, nor are any assemblies read, but the timestamps for each dependent file
    /// are read via the FileSystem.  If the files don't exist, then a default timestamp is used.
    ///
    /// The operation returns None only if it is not possible to create an IncrementalBuilder for the project at all, e.g. if there
    /// are fatal errors in the options for the project.
    abstract TryGetLogicalTimeStamp: cache: TimeStampCache -> DateTime option

type AssemblyReference =
    | AssemblyReference of range: range * text: string * projectReference: IProjectReference option

    member Range: range

    member Text: string

    member ProjectReference: IProjectReference option

    member SimpleAssemblyNameIs: string -> bool

type UnresolvedAssemblyReference = UnresolvedAssemblyReference of string * AssemblyReference list

[<RequireQualifiedAccess>]
type CompilerTarget =
    | WinExe

    | ConsoleExe

    | Dll

    | Module

    member IsExe: bool

[<RequireQualifiedAccess>]
type CopyFSharpCoreFlag =

    | Yes

    | No

/// Represents the file or string used for the --version flag
type VersionFlag =

    | VersionString of string

    | VersionFile of string

    | VersionNone

    member GetVersionInfo: implicitIncludeDir: string -> ILVersionInfo

    member GetVersionString: implicitIncludeDir: string -> string

type Directive =
    | Resolution

    | Include

type LStatus =
    | Unprocessed

    | Processed

type TokenizeOption =
    | AndCompile

    | Only

    | Debug

    | Unfiltered

type PackageManagerLine =
    { Directive: Directive
      LineStatus: LStatus
      Line: string
      Range: range }

    static member AddLineWithKey:
        packageKey: string ->
        directive: Directive ->
        line: string ->
        m: range ->
        packageManagerLines: Map<string, PackageManagerLine list> ->
            Map<string, PackageManagerLine list>

    static member RemoveUnprocessedLines:
        packageKey: string ->
        packageManagerLines: Map<string, PackageManagerLine list> ->
            Map<string, PackageManagerLine list>

    static member SetLinesAsProcessed:
        packageKey: string ->
        packageManagerLines: Map<string, PackageManagerLine list> ->
            Map<string, PackageManagerLine list>

    static member StripDependencyManagerKey: packageKey: string -> line: string -> string

[<RequireQualifiedAccess>]
type MetadataAssemblyGeneration =
    | None

    /// Includes F# signature and optimization metadata as resources in the emitting assembly.
    /// Implementation assembly will still be emitted normally, but will emit the reference assembly with the specified output path.
    | ReferenceOut of outputPath: string

    /// Includes F# signature and optimization metadata as resources in the emitting assembly.
    /// Only emits the assembly as a reference assembly.
    | ReferenceOnly

[<RequireQualifiedAccess>]
type ParallelReferenceResolution =
    | On
    | Off

/// Determines the algorithm used for type-checking.
[<RequireQualifiedAccess>]
type TypeCheckingMode =
    /// Default mode where all source files are processed sequentially in compilation order.
    | Sequential
    /// Parallel type-checking that uses automated file-to-file dependency detection to construct a file graph processed in parallel.
    | Graph

/// Some of the information dedicated to type-checking.
[<RequireQualifiedAccess>]
type TypeCheckingConfig =
    {
        Mode: TypeCheckingMode
        /// When using TypeCheckingMode.Graph, this flag determines whether the
        /// resolved file graph should be serialised as a Mermaid diagram into a file next to the output dll.
        DumpGraph: bool
    }

[<NoEquality; NoComparison>]
type TcConfigBuilder =
    {
        mutable primaryAssembly: PrimaryAssembly

        mutable noFeedback: bool

        mutable stackReserveSize: int32 option

        mutable implicitIncludeDir: string

        mutable openDebugInformationForLaterStaticLinking: bool

        defaultFSharpBinariesDir: string

        mutable compilingFSharpCore: bool

        mutable useIncrementalBuilder: bool

        mutable includes: string list

        mutable implicitOpens: string list

        mutable useFsiAuxLib: bool

        mutable implicitlyReferenceDotNetAssemblies: bool

        mutable resolutionEnvironment: LegacyResolutionEnvironment

        mutable implicitlyResolveAssemblies: bool

        /// Set if the user has explicitly turned indentation-aware syntax on/off
        mutable indentationAwareSyntax: bool option

        mutable conditionalDefines: string list

        /// Sources added into the build with #load
        mutable loadedSources: (range * string * string) list

        mutable compilerToolPaths: string list

        mutable referencedDLLs: AssemblyReference list

        mutable packageManagerLines: Map<string, PackageManagerLine list>

        mutable projectReferences: IProjectReference list

        mutable knownUnresolvedReferences: UnresolvedAssemblyReference list

        reduceMemoryUsage: ReduceMemoryFlag

        mutable subsystemVersion: int * int

        mutable useHighEntropyVA: bool

        mutable inputCodePage: int option

        mutable clearResultsCache: bool

        mutable embedResources: string list

        mutable diagnosticsOptions: FSharpDiagnosticOptions

        mutable mlCompatibility: bool

        mutable checkNullness: bool

        mutable checkOverflow: bool

        mutable showReferenceResolutions: bool

        mutable outputDir: string option

        mutable outputFile: string option

        mutable platform: ILPlatform option

        mutable prefer32Bit: bool

        mutable useSimpleResolution: bool

        mutable target: CompilerTarget

        mutable debuginfo: bool

        mutable testFlagEmitFeeFeeAs100001: bool

        mutable dumpDebugInfo: bool

        mutable debugSymbolFile: string option

        mutable typeCheckOnly: bool

        mutable parseOnly: bool

        mutable importAllReferencesOnly: bool

        mutable simulateException: string option

        mutable printAst: bool

        mutable tokenize: TokenizeOption

        mutable testInteractionParser: bool

        mutable reportNumDecls: bool

        mutable printSignature: bool

        mutable printSignatureFile: string

        mutable printAllSignatureFiles: bool

        mutable xmlDocOutputFile: string option

        mutable stats: bool

        mutable generateFilterBlocks: bool

        mutable signer: string option

        mutable container: string option

        mutable delaysign: bool

        mutable publicsign: bool

        mutable version: VersionFlag

        mutable metadataVersion: string option

        mutable standalone: bool

        mutable extraStaticLinkRoots: string list

        mutable compressMetadata: bool

        mutable noSignatureData: bool

        mutable onlyEssentialOptimizationData: bool

        mutable useOptimizationDataFile: bool

        mutable jitTracking: bool

        mutable portablePDB: bool

        mutable embeddedPDB: bool

        mutable embedAllSource: bool

        mutable embedSourceList: string list

        mutable sourceLink: string

        mutable internConstantStrings: bool

        mutable extraOptimizationIterations: int

        mutable win32icon: string

        mutable win32res: string

        mutable win32manifest: string

        mutable includewin32manifest: bool

        mutable linkResources: string list

        mutable legacyReferenceResolver: LegacyReferenceResolver

        mutable showFullPaths: bool

        mutable diagnosticStyle: DiagnosticStyle

        mutable utf8output: bool

        mutable flatErrors: bool

        mutable maxErrors: int

        mutable abortOnError: bool

        mutable baseAddress: int32 option

        mutable checksumAlgorithm: HashAlgorithm

#if DEBUG
        mutable showOptimizationData: bool
#endif

        mutable showTerms: bool

        mutable writeTermsToFiles: bool

        mutable doDetuple: bool

        mutable doTLR: bool

        mutable doFinalSimplify: bool

        mutable optsOn: bool

        mutable optSettings: Optimizer.OptimizationSettings

        mutable emitTailcalls: bool

        mutable deterministic: bool

        mutable concurrentBuild: bool

        mutable parallelIlxGen: bool

        mutable emitMetadataAssembly: MetadataAssemblyGeneration

        mutable preferredUiLang: string option

        mutable lcid: int option

        mutable productNameForBannerText: string

        mutable showBanner: bool

        mutable showTimes: bool

        mutable writeTimesToFile: string option

        mutable showLoadedAssemblies: bool

        mutable continueAfterParseFailure: bool

#if !NO_TYPEPROVIDERS
        mutable showExtensionTypeMessages: bool
#endif

        mutable pause: bool

        mutable alwaysCallVirt: bool

        mutable noDebugAttributes: bool

        mutable useReflectionFreeCodeGen: bool

        /// If true, indicates all type checking and code generation is in the context of fsi.exe
        isInteractive: bool

        isInvalidationSupported: bool

        mutable emitDebugInfoInQuotations: bool

        mutable strictIndentation: bool option

        mutable exename: string option

        mutable copyFSharpCore: CopyFSharpCoreFlag

        mutable shadowCopyReferences: bool

        mutable useSdkRefs: bool

        mutable fxResolver: FxResolver option

        mutable bufferWidth: int option

        mutable fsiMultiAssemblyEmit: bool

        rangeForErrors: range

        sdkDirOverride: string option

        /// A function to call to try to get an object that acts as a snapshot of the metadata section of a .NET binary,
        /// and from which we can read the metadata. Only used when metadataOnly=true.
        mutable tryGetMetadataSnapshot: ILReaderTryGetMetadataSnapshot

        /// if true - 'let mutable x = Span.Empty', the value 'x' is a stack referring span. Used for internal testing purposes only until we get true stack spans.
        mutable internalTestSpanStackReferring: bool

        /// Prevent erasure of conditional attributes and methods so tooling is able analyse them.
        mutable noConditionalErasure: bool

        /// Take '#line' into account? Defaults to true
        mutable applyLineDirectives: bool

        mutable pathMap: PathMap

        mutable langVersion: LanguageVersion

        mutable xmlDocInfoLoader: IXmlDocumentationInfoLoader option

        mutable exiter: Exiter

        mutable parallelReferenceResolution: ParallelReferenceResolution

        mutable captureIdentifiersWhenParsing: bool

        mutable typeCheckingConfig: TypeCheckingConfig

        mutable dumpSignatureData: bool

        mutable realsig: bool
    }

    static member CreateNew:
        legacyReferenceResolver: LegacyReferenceResolver *
        defaultFSharpBinariesDir: string *
        reduceMemoryUsage: ReduceMemoryFlag *
        implicitIncludeDir: string *
        isInteractive: bool *
        isInvalidationSupported: bool *
        defaultCopyFSharpCore: CopyFSharpCoreFlag *
        tryGetMetadataSnapshot: ILReaderTryGetMetadataSnapshot *
        sdkDirOverride: string option *
        rangeForErrors: range ->
            TcConfigBuilder

    member DecideNames: string list -> string * string option * string

    member TurnWarningOff: range * string -> unit

    member TurnWarningOn: range * string -> unit

    member AddIncludePath: range * string * string -> unit

    member AddCompilerToolsByPath: string -> unit

    member AddReferencedAssemblyByPath: range * string -> unit

    member RemoveReferencedAssemblyByPath: range * string -> unit

    member AddEmbeddedSourceFile: string -> unit

    member AddEmbeddedResource: string -> unit

    member AddPathMapping: oldPrefix: string * newPrefix: string -> unit

    static member SplitCommandLineResourceInfo: string -> string * string * ILResourceAccess

    // Directories to start probing in for native DLLs for FSI dynamic loading
    member GetNativeProbingRoots: unit -> seq<string>

    member AddReferenceDirective:
        dependencyProvider: DependencyProvider * m: range * path: string * directive: Directive -> unit

    member AddLoadedSource: m: range * originalPath: string * pathLoadedFrom: string -> unit

    member FxResolver: FxResolver

    member SetUseSdkRefs: useSdkRefs: bool -> unit

    member SetPrimaryAssembly: primaryAssembly: PrimaryAssembly -> unit

/// Immutable TcConfig, modifications are made via a TcConfigBuilder
[<Sealed>]
type TcConfig =
    member primaryAssembly: PrimaryAssembly

    member noFeedback: bool

    member stackReserveSize: int32 option

    member implicitIncludeDir: string

    member openDebugInformationForLaterStaticLinking: bool

    member fsharpBinariesDir: string

    member compilingFSharpCore: bool

    member useIncrementalBuilder: bool

    member includes: string list

    member implicitOpens: string list

    member useFsiAuxLib: bool

    member implicitlyReferenceDotNetAssemblies: bool

    member implicitlyResolveAssemblies: bool

    /// Set if the user has explicitly turned indentation-aware syntax on/off
    member indentationAwareSyntax: bool option

    member conditionalDefines: string list

    member subsystemVersion: int * int

    member useHighEntropyVA: bool

    member compilerToolPaths: string list

    member referencedDLLs: AssemblyReference list

    member reduceMemoryUsage: ReduceMemoryFlag

    member inputCodePage: int option

    member clearResultsCache: bool

    member embedResources: string list

    member diagnosticsOptions: FSharpDiagnosticOptions

    member mlCompatibility: bool

    member checkNullness: bool

    member checkOverflow: bool

    member showReferenceResolutions: bool

    member outputDir: string option

    member outputFile: string option

    member platform: ILPlatform option

    member prefer32Bit: bool

    member useSimpleResolution: bool

    member target: CompilerTarget

    member debuginfo: bool

    member testFlagEmitFeeFeeAs100001: bool

    member dumpDebugInfo: bool

    member debugSymbolFile: string option

    member typeCheckOnly: bool

    member parseOnly: bool

    member importAllReferencesOnly: bool

    member simulateException: string option

    member printAst: bool

    member tokenize: TokenizeOption

    member testInteractionParser: bool

    member reportNumDecls: bool

    member printSignature: bool

    member printSignatureFile: string

    member printAllSignatureFiles: bool

    member xmlDocOutputFile: string option

    member stats: bool

    member generateFilterBlocks: bool

    member signer: string option

    member container: string option

    member delaysign: bool

    member publicsign: bool

    member version: VersionFlag

    member metadataVersion: string option

    member standalone: bool

    member extraStaticLinkRoots: string list

    member compressMetadata: bool

    member noSignatureData: bool

    member onlyEssentialOptimizationData: bool

    member useOptimizationDataFile: bool

    member jitTracking: bool

    member portablePDB: bool

    member embeddedPDB: bool

    member embedAllSource: bool

    member embedSourceList: string list

    member sourceLink: string

    member internConstantStrings: bool

    member extraOptimizationIterations: int

    member win32icon: string

    member win32res: string

    member win32manifest: string

    member includewin32manifest: bool

    member linkResources: string list

    member showFullPaths: bool

    member diagnosticStyle: DiagnosticStyle

    member utf8output: bool

    member flatErrors: bool

    member maxErrors: int

    member baseAddress: int32 option

    member checksumAlgorithm: HashAlgorithm

#if DEBUG
    member showOptimizationData: bool
#endif

    member showTerms: bool

    member writeTermsToFiles: bool

    member doDetuple: bool

    member doTLR: bool

    member doFinalSimplify: bool

    member optSettings: Optimizer.OptimizationSettings

    member emitTailcalls: bool

    member deterministic: bool

    member concurrentBuild: bool

    member parallelIlxGen: bool

    member emitMetadataAssembly: MetadataAssemblyGeneration

    member pathMap: PathMap

    member preferredUiLang: string option

    member optsOn: bool

    member productNameForBannerText: string

    member showBanner: bool

    member showTimes: bool

    member writeTimesToFile: string option

    member showLoadedAssemblies: bool

    member continueAfterParseFailure: bool

#if !NO_TYPEPROVIDERS
    member showExtensionTypeMessages: bool
#endif

    member pause: bool

    member alwaysCallVirt: bool

    member noDebugAttributes: bool

    member useReflectionFreeCodeGen: bool

    /// If true, indicates all type checking and code generation is in the context of fsi.exe
    member isInteractive: bool

    member isInvalidationSupported: bool

    member bufferWidth: int option

    /// Indicates if F# Interactive is using single-assembly emit via Reflection.Emit, where internals are available.
    member fsiMultiAssemblyEmit: bool

    member xmlDocInfoLoader: IXmlDocumentationInfoLoader option

    member FxResolver: FxResolver

    member strictIndentation: bool option

    member ComputeIndentationAwareSyntaxInitialStatus: string -> bool

    member GetTargetFrameworkDirectories: unit -> string list

    /// Get the loaded sources that exist and issue a warning for the ones that don't
    member GetAvailableLoadedSources: unit -> (range * string) list

    member ComputeCanContainEntryPoint: sourceFiles: string list -> bool list * bool

    /// File system query based on TcConfig settings
    member ResolveSourceFile: range * fileName: string * pathLoadedFrom: string -> string

    /// File system query based on TcConfig settings
    member MakePathAbsolute: string -> string

    member resolutionEnvironment: LegacyResolutionEnvironment

    member copyFSharpCore: CopyFSharpCoreFlag

    member shadowCopyReferences: bool

    member useSdkRefs: bool

    member sdkDirOverride: string option

    member legacyReferenceResolver: LegacyReferenceResolver

    member emitDebugInfoInQuotations: bool

    member langVersion: LanguageVersion

    static member Create: TcConfigBuilder * validate: bool -> TcConfig

    member tryGetMetadataSnapshot: ILReaderTryGetMetadataSnapshot

    member targetFrameworkVersion: string

    member knownUnresolvedReferences: UnresolvedAssemblyReference list

    member packageManagerLines: Map<string, PackageManagerLine list>

    member loadedSources: (range * string * string) list

    /// Prevent erasure of conditional attributes and methods so tooling is able analyse them.
    member noConditionalErasure: bool

    /// Take '#line' into account? Defaults to true
    member applyLineDirectives: bool

    /// if true - 'let mutable x = Span.Empty', the value 'x' is a stack referring span. Used for internal testing purposes only until we get true stack spans.
    member internalTestSpanStackReferring: bool

    member GetSearchPathsForLibraryFiles: unit -> string list

    member IsSystemAssembly: string -> bool

    member PrimaryAssemblyDllReference: unit -> AssemblyReference

    member CoreLibraryDllReference: unit -> AssemblyReference

    /// Allow forking and subsequent modification of the TcConfig via a new TcConfigBuilder
    member CloneToBuilder: unit -> TcConfigBuilder

    /// Indicates if the compilation will result in F# signature data resource in the generated binary
    member GenerateSignatureData: bool

    /// Indicates if the compilation will result in an F# optimization data resource in the generated binary
    member GenerateOptimizationData: bool

    /// Check if the primary assembly is mscorlib
    member assumeDotNetFramework: bool

    member exiter: Exiter

    member parallelReferenceResolution: ParallelReferenceResolution

    member captureIdentifiersWhenParsing: bool

    member typeCheckingConfig: TypeCheckingConfig

    member dumpSignatureData: bool

    member realsig: bool

/// Represents a computation to return a TcConfig. Normally this is just a constant immutable TcConfig,
/// but for F# Interactive it may be based on an underlying mutable TcConfigBuilder.
[<Sealed>]
type TcConfigProvider =

    member Get: CompilationThreadToken -> TcConfig

    /// Get a TcConfigProvider which will return only the exact TcConfig.
    static member Constant: TcConfig -> TcConfigProvider

    /// Get a TcConfigProvider which will continue to respect changes in the underlying
    /// TcConfigBuilder rather than delivering snapshots.
    static member BasedOnMutableBuilder: TcConfigBuilder -> TcConfigProvider

val TryResolveFileUsingPaths: paths: string seq * m: range * fileName: string -> string option

val ResolveFileUsingPaths: paths: string seq * m: range * fileName: string -> string

val GetWarningNumber: m: range * warningNumber: string * prefixSupported: bool -> int option

/// Get the name used for FSharp.Core
val GetFSharpCoreLibraryName: unit -> string

/// Signature file suffixes
val FSharpSigFileSuffixes: string list

/// Implementation file suffixes
val FSharpImplFileSuffixes: string list

/// Script file suffixes
val FSharpScriptFileSuffixes: string list

/// File suffixes where #light is the default
val FSharpIndentationAwareSyntaxFileSuffixes: string list

val FSharpMLCompatFileSuffixes: string list

/// Indicates whether experimental features should be enabled automatically
val FSharpExperimentalFeaturesEnabledAutomatically: bool
