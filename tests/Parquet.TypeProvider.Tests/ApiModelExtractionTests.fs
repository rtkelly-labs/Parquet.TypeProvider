namespace Parquet.TypeProvider.Tests

open System
open System.IO
open System.Text.Json
open Xunit

#nowarn "3261"

module ApiModelExtractionTests =

    let rec private findRepoRoot (dir: DirectoryInfo) : string =
        if isNull dir then
            failwith "Could not find repository root"
        elif File.Exists(Path.Combine(dir.FullName, "Parquet.TypeProvider.sln")) then
            dir.FullName
        else
            findRepoRoot dir.Parent

    [<CLIMutable>]
    type MethodTestModel =
        { name: string; summary: string option }

    [<CLIMutable>]
    type TypeTestModel =
        { name: string
          summary: string option
          methods: MethodTestModel[] }

    [<CLIMutable>]
    type NamespaceTestModel =
        { name: string; types: TypeTestModel[] }

    [<CLIMutable>]
    type ApiDocTestModel =
        { projectName: string
          version: string
          namespaces: NamespaceTestModel[] }

    let private verifyApiModel (fileToRead: string) =
        Assert.True(File.Exists(fileToRead))
        let json = File.ReadAllText(fileToRead)
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        let doc = JsonSerializer.Deserialize<ApiDocTestModel>(json, options)
        Assert.NotNull(doc)

        Assert.Equal("Parquet.TypeProvider", doc.projectName)
        Assert.NotEmpty(doc.namespaces)

        let allTypes = doc.namespaces |> Seq.collect (fun n -> n.types) |> Seq.toList

        let typeNames = allTypes |> List.map (fun t -> t.name)

        // Verify core types exist
        Assert.Contains("ParquetColumnBatch", typeNames)
        Assert.Contains("ParquetRow", typeNames)
        Assert.Contains("ParquetSchemaField", typeNames)
        Assert.Contains("ParquetReaderCore", typeNames)
        Assert.Contains("ParquetTypeProvider", typeNames)

        // Verify ParquetRow documentation
        let rowType = allTypes |> List.find (fun t -> t.name = "ParquetRow")
        Assert.True(rowType.summary.IsSome && not (String.IsNullOrWhiteSpace(rowType.summary.Value)))

        // Verify ParquetReaderCore methods and documentation
        let readerCore = allTypes |> List.find (fun t -> t.name = "ParquetReaderCore")
        let methodNames = readerCore.methods |> Array.map (fun m -> m.name)

        Assert.Contains("loadFromFile", methodNames)
        Assert.Contains("readColumnArray", methodNames)

        let loadMethod = readerCore.methods |> Array.find (fun m -> m.name = "loadFromFile")

        Assert.True(
            loadMethod.summary.IsSome
            && not (String.IsNullOrWhiteSpace(loadMethod.summary.Value))
        )

    [<Fact>]
    let ``Extracted API model contains F# public types, members, and XML documentation`` () =
        let repoRoot = findRepoRoot (DirectoryInfo(AppContext.BaseDirectory))
        let artifactFile = Path.Combine(repoRoot, "artifacts", "api-model.json")

        let fileToRead, cleanupDir =
            if File.Exists(artifactFile) then
                artifactFile, None
            else
                let tempDir =
                    Path.Combine(repoRoot, "temp", "tp-api-test-" + Guid.NewGuid().ToString("N"))

                Directory.CreateDirectory(tempDir) |> ignore

                let psi =
                    Diagnostics.ProcessStartInfo(
                        "dotnet",
                        sprintf "run scripts/ExportApiDocs.cs -- --repo \"%s\" --out \"%s\"" repoRoot tempDir
                    )

                psi.WorkingDirectory <- repoRoot
                psi.UseShellExecute <- false
                use p = Diagnostics.Process.Start(psi)

                if not (isNull p) then
                    p.WaitForExit()
                    Assert.Equal(0, p.ExitCode)

                Path.Combine(tempDir, "api-model.json"), Some tempDir

        try
            verifyApiModel fileToRead
        finally
            match cleanupDir with
            | Some d when Directory.Exists(d) ->
                try
                    Directory.Delete(d, true)
                with _ ->
                    ()
            | _ -> ()
