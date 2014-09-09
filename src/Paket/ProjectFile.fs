﻿namespace Paket

open System
open System.IO
open System.Xml
open System.Collections.Generic

/// Contains methods to read and manipulate project file ndoes.
type private InstallInfo = {
    DllName : string
    Path : string
    Condition : FrameworkIdentifier
}

module private InstallRules = 
    let groupDLLs (usedPackages : HashSet<string>) extracted = 
        [ for package, libraries in extracted do
              if usedPackages.Contains package.Name then 
                  let libraries = libraries |> Seq.toArray
                  for (lib : FileInfo) in libraries do
                      let conditions = FrameworkIdentifier.DetectFromPath lib.FullName
                      for condition in conditions do
                          yield { DllName = lib.Name.Replace(lib.Extension, "")
                                  Path = lib.FullName
                                  Condition = condition } ]
        |> Seq.groupBy (fun info -> info.DllName, info.Condition.GetGroupCondition())
        |> Seq.groupBy (fun ((name, _), _) -> name)

    let handlePath root (libs:InstallInfo list) =
        libs 
        |> List.map (fun lib -> { lib with Path = Uri(root).MakeRelativeUri(Uri(lib.Path)).ToString().Replace("/", "\\")} )


/// Contains methods to read and manipulate project files.
type ProjectFile = 
    { FileName: string
      OriginalText : string
      Document : XmlDocument
      Namespaces : XmlNamespaceManager }
    static member DefaultNameSpace = "http://schemas.microsoft.com/developer/msbuild/2003"

    member this.DeleteIfEmpty xPath =
        for node in this.Document.SelectNodes(xPath, this.Namespaces) do
            if node.ChildNodes.Count = 0 then
                node.ParentNode.RemoveChild(node) |> ignore

    member this.HasCustomNodes(dllName) =
        let hasCustom = ref false
        for node in this.Document.SelectNodes("//ns:Reference", this.Namespaces) do
            if node.Attributes.["Include"].InnerText.Split(',').[0] = dllName then
                let isPaket = ref false
                for child in node.ChildNodes do
                    if child.Name = "Paket" then 
                        isPaket := true
                if not !isPaket then
                    hasCustom := true
            
        !hasCustom

    member this.DeletePaketNodes() =
        for node in this.Document.SelectNodes("//ns:Reference", this.Namespaces) do
            let remove = ref false
            for child in node.ChildNodes do
                if child.Name = "Paket" then remove := true
            
            if !remove then
                node.ParentNode.RemoveChild(node) |> ignore

    member this.DeleteEmptyReferences() =
        this.DeleteIfEmpty("//ns:Project/ns:Choose/ns:Otherwise/ns:ItemGroup")        
        this.DeleteIfEmpty("//ns:Project/ns:Choose/ns:When/ns:ItemGroup")
        this.DeleteIfEmpty("//ns:Project/ns:Choose/ns:Otherwise")
        this.DeleteIfEmpty("//ns:Project/ns:Choose/ns:When")
        this.DeleteIfEmpty("//ns:Project/ns:Choose")

    member this.UpdateReferences(extracted,usedPackages:HashSet<string>) =
        this.DeletePaketNodes()

        let projectNode =
            seq { for node in this.Document.SelectNodes("//ns:Project", this.Namespaces) -> node }
            |> Seq.head

        let installInfos = InstallRules.groupDLLs usedPackages extracted
        for dllName,libsWithSameName in installInfos do
            if this.HasCustomNodes(dllName) then () else            
            let lastLib = ref None
            for (_,_),libs in libsWithSameName do
                let chooseNode = this.Document.CreateElement("Choose", ProjectFile.DefaultNameSpace)
                let libsWithSameFrameworkVersion = 
                    libs 
                    |> List.ofSeq                    
                    |> InstallRules.handlePath this.FileName
                    |> List.sortBy (fun lib -> lib.Path)

                for lib in libsWithSameFrameworkVersion do                
                    let whenNode = this.Document.CreateElement("When", ProjectFile.DefaultNameSpace)
                    whenNode.SetAttribute("Condition", lib.Condition.GetCondition()) |> ignore
                        
                    let reference = this.Document.CreateElement("Reference", ProjectFile.DefaultNameSpace)
                    reference.SetAttribute("Include", lib.DllName)

                    let element = this.Document.CreateElement("HintPath",ProjectFile.DefaultNameSpace)
                    element.InnerText <- lib.Path
            
                    reference.AppendChild(element) |> ignore
 
                    let element = this.Document.CreateElement("Private",ProjectFile.DefaultNameSpace)
                    element.InnerText <- "True"
                    reference.AppendChild(element) |> ignore

                    let element = this.Document.CreateElement("Paket",ProjectFile.DefaultNameSpace)
                    element.InnerText <- "True"            
                    reference.AppendChild(element) |> ignore

                    let itemGroup = this.Document.CreateElement("ItemGroup", ProjectFile.DefaultNameSpace)
                    itemGroup.AppendChild(reference) |> ignore
                    whenNode.AppendChild(itemGroup) |> ignore
                    chooseNode.AppendChild(whenNode) |> ignore

                    lastLib := Some lib

                match !lastLib with
                | None -> ()
                | Some lib ->
                    let whenNode = this.Document.CreateElement("When", ProjectFile.DefaultNameSpace)
                    whenNode.SetAttribute("Condition", lib.Condition.GetGroupCondition()) |> ignore

                    let reference = this.Document.CreateElement("Reference", ProjectFile.DefaultNameSpace)
                    reference.SetAttribute("Include", lib.DllName)

                    let element = this.Document.CreateElement("HintPath",ProjectFile.DefaultNameSpace)
                    element.InnerText <- lib.Path
            
                    reference.AppendChild(element) |> ignore
 
                    let element = this.Document.CreateElement("Private",ProjectFile.DefaultNameSpace)
                    element.InnerText <- "True"
                    reference.AppendChild(element) |> ignore

                    let element = this.Document.CreateElement("Paket",ProjectFile.DefaultNameSpace)
                    element.InnerText <- "True"            
                    reference.AppendChild(element) |> ignore

                    let itemGroup = this.Document.CreateElement("ItemGroup", ProjectFile.DefaultNameSpace)
                    itemGroup.AppendChild(reference) |> ignore
                    whenNode.AppendChild(itemGroup) |> ignore
                    chooseNode.AppendChild(whenNode) |> ignore

                projectNode.AppendChild(chooseNode) |> ignore

        this.DeleteEmptyReferences()

        if Utils.normalizeXml this.Document <> this.OriginalText then
            this.Document.Save(this.FileName)

    static member Load(fileName:string) =
        let fi = FileInfo(fileName)
        let doc = new XmlDocument()
        doc.Load fi.FullName

        let manager = new XmlNamespaceManager(doc.NameTable)
        manager.AddNamespace("ns", ProjectFile.DefaultNameSpace)
        { FileName = fi.FullName; Document = doc; Namespaces = manager; OriginalText = Utils.normalizeXml doc }