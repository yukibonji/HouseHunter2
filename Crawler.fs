﻿namespace HouseHunter

open System
open FSharp.Data
open HtmlAgilityPack

type LatLong = 
    { Lat : float
      Long : float } 
    static member parse lat long =
        { Lat = Double.Parse lat
          Long = Double.Parse long }
    static member fromAddress str = async {
        let url = "http://maps.googleapis.com/maps/api/geocode/xml?sensor=false&address=" + (Uri.EscapeDataString str)
        let! geocodeResponse = XmlProvider<"http://maps.googleapis.com/maps/api/geocode/xml?sensor=false&address=london">.AsyncLoad url
        let location = geocodeResponse.Results.[0].Geometry.Location
        return { Lat = double location.Lat
                 Long = double location.Lng } }
    override x.ToString() = 
        sprintf "%f,%f" x.Lat x.Long
    static member Default = { Lat = 0.; Long = 0. }

type Property =
    { Url : string
      Name : string
      Description : string
      Address : string
      LatLong : LatLong
      Price : decimal
      Photos : string list
      Links : (string*string) list
      Features : string list
      Nearby : string list
      Phone : string
      AddedOn : string }

    override x.ToString() = sprintf "%A" x

    member x.GetSearchableContent() =
        [ x.Name; x.Description; x.Address ] @ x.Features @ x.Nearby
        |> List.filter ((<>) "")

    static member Mock = 
        { Url = "http://example.org"
          Name = ""
          Description = ""
          Address = ""
          LatLong = LatLong.Default
          Price = 1000M
          Photos = []
          Links = []
          Features = []
          Nearby = [ "London Victoria (0.1 miles)" ]
          Phone = "02080000000"
          AddedOn = "3rd May 2014" }

type IPropertySite =

    abstract GetFirstListingPages : minPrice:decimal * maxPrice:decimal * minBeds:int * maxBeds:int -> string list
    abstract ParseListingPage : doc:HtmlDocument -> properties:Property seq * nextPageUrl:string option
    abstract ParsePropertyPage : doc:HtmlDocument -> property:Property -> Property

type Crawler(processedPropertyUrls, addProperty, propertySites) = 

    let processedPropertyUrls = ref (Set.ofList processedPropertyUrls)
    
    let rec processPage (propertySite:IPropertySite) url = Async.CatchAndLog url <| async {

        let! html = Http.AsyncRequestStringWithRetriesAndLogging url
        let doc = HtmlDocument.Parse html
    
        let properties, nextPageUrl = propertySite.ParseListingPage doc

        let childJobs = 
            properties 
            |> Seq.where (fun p -> not <| (!processedPropertyUrls).Contains p.Url)
            |> Seq.map (fun property -> Async.CatchAndLog property.Url <| async {

                let url = property.Url
                    
                let! html = Http.AsyncRequestStringWithRetriesAndLogging url
                let doc = HtmlDocument.Parse html

                let property = propertySite.ParsePropertyPage doc property
                    
                // this bit should not be cancelled as part of the parent:
                async {
                    do! addProperty property

                    lock processedPropertyUrls <| fun () ->
                        processedPropertyUrls := (!processedPropertyUrls).Add property.Url
                } |> Async.CatchAndLog ("addProperty " + url) |> Async.Start
            })

        for job in childJobs do
            do! job |> Async.StartChild |> Async.Ignore
        
        match nextPageUrl with
        | Some nextPageUrl -> return! processPage propertySite nextPageUrl
        | None -> ()
    }

    member x.Crawl args =
        propertySites
        |> List.collect (fun (propertySite:IPropertySite) -> propertySite.GetFirstListingPages args |> List.map (processPage propertySite))
        |> Async.Parallel
        |> Async.Ignore