﻿[<AutoOpen>]
module XTract.Scraper

open System.Collections.Concurrent
open System
open Newtonsoft.Json
open Deedle
open Microsoft.FSharp.Reflection
open System.IO
open CsvHelper
open SpreadSharp
open SpreadSharp.Collections
open Extraction
open Helpers

type Scraper<'T when 'T : equality>(extractors) =
    let dataStore = ConcurrentBag<Source * 'T>()
    let failedRequests = ConcurrentQueue<string>()
    let log = ConcurrentQueue<string>()
    let mutable loggingEnabled = true

    let logger =
        MailboxProcessor.Start(fun inbox ->
            let rec loop() =
                async {
                    let! msg = inbox.Receive()
                    let time = DateTime.Now.ToString()
                    let msg' = time + " - " + msg
                    log.Enqueue msg'
                    match loggingEnabled with
                    | false -> ()
                    | true -> printfn "%s" msg'
                    return! loop()
                }
            loop()
        )
    
    /// Posts a new log message to the scraper's logging agent.
    member __.Log msg = logger.Post msg

    /// Returns the scraper's log.
    member __.LogData = log.ToArray()

    /// Enables or disables printing log messages.
    member __.WithLogging enabled = loggingEnabled <- enabled

    /// Scrapes a single data item from the specified URL or HTML code.
    member __.Scrape input =
        match input with
        | Utils.Html ->
            logger.Post "Scraping data from HTML code"
            let record = Utils.scrape<'T> input extractors
            match record with
            | None -> None
            | Some x ->
                dataStore.Add(Html, x)
                record
        | Utils.Url ->
            logger.Post <| "Scraping data from " + input
            let html = Http.get input
            match html with
            | None ->
                failedRequests.Enqueue input
                None
            | Some html ->
                let record = Utils.scrape<'T> html extractors
                match record with
                | None -> None
                | Some x ->
                    dataStore.Add(Url input, x)
                    record

    /// Scrapes all the data items from the specified URL or HTML code.
    member __.ScrapeAll input = 
        match input with
        | Utils.Html ->
            logger.Post <| "Scraping data from HTML code"                
            let records = Utils.scrapeAll<'T> input extractors
            match records with
            | None -> None
            | Some lst ->
                lst |> List.iter (fun x -> dataStore.Add(Html, x))
                records
        | Utils.Url ->
            logger.Post <| "Scraping data from " + input
            let html = Http.get input
            match html with
            | None ->
                failedRequests.Enqueue input
                None
            | Some html ->
                let records = Utils.scrapeAll<'T> html extractors
                match records with
                | None -> None
                | Some lst ->
                    lst |> List.iter (fun x -> dataStore.Add(Url input, x))
                    records

    /// Throttles scraping a single data item from the specified URLs
    /// by sending 5 concurrent requests and executes the async computation
    /// once done.
    member __.ThrottleScrape urls doneAsync =
        let asyncs =
            urls
            |> Seq.map (fun x ->
                async {
                    logger.Post <| "Scraping " + x
                    let! html = Http.getAsync x
                    match html with
                    | None ->
                        failedRequests.Enqueue x
                    | Some html ->
                        let record = Utils.scrape<'T> html extractors
                        match record with
                        | None -> ()
                        | Some r -> dataStore.Add(Url x, r)
                }                
            )
        Throttler.throttle asyncs 5 doneAsync

    /// Throttles scraping all the data items from the specified URLs
    /// by sending 5 concurrent requests and executes the async computation
    /// once done.
    member __.ThrottleScrapeAll urls doneAsync = 
        let asyncs =
            urls
            |> Seq.map (fun x ->
                async {
                    let! html = Http.getAsync x
                    match html with
                    | None ->
                        failedRequests.Enqueue x
                    | Some html ->
                        let records = Utils.scrapeAll<'T> html extractors
                        match records with
                        | None -> ()
                        | Some lst ->
                            lst |> List.iter (fun r -> dataStore.Add(Url x, r))
                }                
            )
        Throttler.throttle asyncs 5 doneAsync

    /// Returns the data stored so far by the scraper.
    member __.Data =
        dataStore
        |> Seq.distinctBy snd
        |> Seq.toArray

    /// Returns the urls that scraper failed to download.
    member __.FailedRequests = failedRequests.ToArray()

    /// Stores a failed HTTP request, use this method when
    /// handling HTTP requests by yourself and you want to
    /// track errors.
    member __.StoreFailedRequest url = failedRequests.Enqueue url

    /// Returns the data stored so far by the scraper in JSON format.
    member __.JsonData =
        let records = dataStore |> Seq.map snd
        JsonConvert.SerializeObject(records, Formatting.Indented)

    /// Returns the data stored so far by the scraper as a Deedle data frame.
    member __.DataFrame =
        let records = dataStore |> Seq.map snd        
        Frame.ofRecords records

    /// Saves the data stored by the scraper in a CSV file.
    member __.SaveCsv(path) =
        let headers =
            FSharpType.GetRecordFields typeof<'T>
            |> Array.map (fun x -> x.Name)
            |> fun x -> Array.append x [|"Source"|]
        let sw = File.CreateText(path)
        let csv = new CsvWriter(sw)
        headers
        |> Array.iter (fun x -> csv.WriteField x)
        csv.NextRecord()
        dataStore
        |> Seq.iter (fun x ->
            Utils.fields x
            |> Array.iter (fun x -> csv.WriteField x)
            csv.NextRecord()            
        )
        sw.Flush()
        sw.Dispose()

    /// Saves the data stored by the scraper in an Excel file.
    member __.SaveExcel(path) =
        let xl = XlApp.startHidden()
        let workbook = XlWorkbook.add xl
        let worksheet = XlWorksheet.byIndex workbook 1
        let headers =
            FSharpType.GetRecordFields typeof<'T>
            |> Array.map (fun x -> x.Name)
        let columnsCount = Array.length headers + 1
        let rangeString = String.concat "" ["A1:"; string (char (columnsCount + 64)) + "1"]
        let rng = XlRange.get worksheet rangeString
        Array.toRange rng (Array.append headers [|"Source"|])
        dataStore
        |> Seq.iteri (fun idx x ->
            let idxString = string <| idx + 2
            let rangeString = String.concat "" ["A"; idxString; ":"; string (char (columnsCount + 64)); idxString]
            let rng = XlRange.get worksheet rangeString
            Array.toRange rng (Utils.fields x)
        )
        XlWorkbook.saveAs workbook path
        XlApp.quit xl
