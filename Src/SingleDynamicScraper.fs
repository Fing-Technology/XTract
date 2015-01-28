﻿[<AutoOpen>]
module XTract.SingleDynamicScraper

open System.Collections.Concurrent
open System.Collections.Generic
open System
open Newtonsoft.Json
open Deedle
open Microsoft.FSharp.Reflection
open System.IO
open CsvHelper
open SpreadSharp
open SpreadSharp.Collections
open Extraction
open OpenQA.Selenium.Chrome
open Settings
open Helpers
open Crawler
open OpenQA.Selenium.PhantomJS
open OpenQA.Selenium.Remote

type SingleDynamicScraper<'T when 'T : equality>(extractors, ?Browser) =
    let browser = defaultArg Browser Phantom
    let driver =
        match browser with
            | Chrome -> new ChromeDriver(XTractSettings.chromeDriverDirectory) :> RemoteWebDriver
            | Phantom -> new PhantomJSDriver(XTractSettings.phantomDriverDirectory) :> RemoteWebDriver
//    let load (url:string) = driver.Navigate().GoToUrl url
    let failedRequests = Queue<string>()

    let rec waitComplete() =
        let state = driver.ExecuteScript("return document.readyState;").ToString()
        match state with
        | "complete" -> ()
        | _ -> waitComplete()
    
    let load (url:string) =
        try
            driver.Navigate().GoToUrl url
            waitComplete()
            Some driver.PageSource
        with _ ->
            failedRequests.Enqueue url
            None

    let dataStore = HashSet<'T>()
//    let failedRequests = ConcurrentQueue<string>()
    let log = ConcurrentQueue<string>()
    let mutable loggingEnabled = true
    let mutable pipelineFunc = (fun (record:'T) -> record)

    let pipeline =
        MailboxProcessor.Start(fun inbox ->
            let rec loop() =
                async {
                    let! msg = inbox.Receive()
                    pipelineFunc msg
                    |> dataStore.Add
                    |> ignore
                    return! loop()
                }
            loop()
        )

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
    
    member __.WithPipeline f = pipelineFunc <- f

    /// Loads the specified URL.
    member __.Get (url:string) =
        load url |> ignore
        waitComplete()

    /// Selects an element and types the specified text into it.
    member __.SendKeysCss cssSelector text =
        let elem = driver.FindElementByCssSelector cssSelector
        printfn "%A" elem.Displayed
        elem.SendKeys(text)

    /// Selects an element and types the specified text into it.
    member __.SendKeysXpath xpath text =
        let elem = driver.FindElementByXPath xpath
        printfn "%A" elem.Displayed
        elem.SendKeys(text)

    /// Selects an element and clicks it.
    member __.ClickCss cssSelector =
        driver.FindElementByCssSelector(cssSelector).Click()
        waitComplete()

    member __.CssSelect cssSelector =
        Html.load driver.PageSource
        |> fun x -> Html.cssSelect x cssSelector

    member __.CssSelectAll cssSelector =
        Html.load driver.PageSource
        |> fun x -> Html.cssSelectAll x cssSelector

    member __.XpathSelect xpath =
        Html.load driver.PageSource
        |> fun x -> Html.xpathSelect x xpath

    member __.XpathSelectAll xpath =
        Html.load driver.PageSource
        |> fun x -> Html.xpathSelectAll x xpath

    member __.PageSource = driver.PageSource

    /// Selects an element and clicks it.
    member __.ClickXpath xpath =
        driver.FindElementByXPath(xpath).Click()
        waitComplete()

    /// Closes the browser drived by the scraper.
    member __.Quit() = driver.Quit()

    /// Posts a new log message to the scraper's logging agent.
    member __.Log msg = logger.Post msg

    /// Returns the scraper's log.
    member __.LogData = log.ToArray()

    /// Enables or disables printing log messages.
    member __.WithLogging enabled = loggingEnabled <- enabled

    /// Scrapes a single data item from the specified URL or HTML code.
    member __.Scrape() =
        let url = driver.Url
        logger.Post <| "Scraping data from " + url
        let html = driver.PageSource
        let record = Utils.scrape<'T> html extractors url
        match record with
        | None -> None
        | Some x ->
            pipeline.Post x
            record

    /// Scrapes a single data item from the specified URL or HTML code.
    member __.Scrape(url) =
        logger.Post <| "Scraping data from " + url
        let html = load url
        match html with
        | None -> None
        | Some html ->
            let record = Utils.scrape<'T> html extractors url
            match record with
            | None -> None
            | Some x ->
                pipeline.Post x
                record

    /// Scrapes a single data item from the specified URL or HTML code.
    member __.Scrape(urls) =
        for url in urls do
            logger.Post <| "Scraping data from " + url
            let html = load url
            match html with
            | None -> ()
            | Some html ->
                let record = Utils.scrape<'T> html extractors url
                match record with
                | None -> ()
                | Some x -> pipeline.Post x

    /// Scrapes all the data items from the specified URL or HTML code.    
    member __.ScrapeAll() =
        let url = driver.Url
        logger.Post <| "Scraping data from " + url
        let html = driver.PageSource
        let records = Utils.scrapeAll<'T> html extractors url
        match records with
        | None -> None
        | Some lst ->
            lst |> List.iter pipeline.Post
            records

    member __.ScrapeAll(url) =
        logger.Post <| "Scraping " + url
        let html = load url
        match html with
        | None -> None
        | Some html ->
            let records = Utils.scrapeAll<'T> html extractors url
            match records with
            | None -> None
            | Some lst ->
                lst |> List.iter pipeline.Post
                records

    member __.ScrapeAll(urls) =
        for url in urls do
            logger.Post <| "Scraping " + url
            let html = load url
            match html with
            | None -> ()
            | Some html ->
                let records = Utils.scrapeAll<'T> html extractors url
                match records with
                | None -> ()
                | Some lst -> lst |> List.iter pipeline.Post

    /// Returns the data stored so far by the scraper.
    member __.Data =
        dataStore
        |> Seq.toArray

    /// Returns the urls that scraper failed to download.
    member __.FailedRequests = failedRequests.ToArray()

//    /// Stores a failed HTTP request, use this method when
//    /// handling HTTP requests by yourself and you want to
//    /// track errors.
//    member __.StoreFailedRequest url = failedRequests.Enqueue url
//
    /// Returns the data stored so far by the scraper in JSON format.
    member __.JsonData =
        JsonConvert.SerializeObject(dataStore, Formatting.Indented)

    /// Returns the data stored so far by the scraper as a Deedle data frame.
    member __.DataFrame = Frame.ofRecords dataStore

    /// Saves the data stored by the scraper in a CSV file.
    member __.SaveCsv(path) =
        let headers =
            FSharpType.GetRecordFields typeof<'T>
            |> Array.map (fun x -> x.Name)
//            |> fun x -> Array.append x [|"Source"|]
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
        let columnsCount = Array.length headers
        let rangeString = String.concat "" ["A1:"; string (char (columnsCount + 64)) + "1"]
        let rng = XlRange.get worksheet rangeString
        Array.toRange rng headers
        dataStore
        |> Seq.iteri (fun idx x ->
            let idxString = string <| idx + 2
            let rangeString = String.concat "" ["A"; idxString; ":"; string (char (columnsCount + 64)); idxString]
            let rng = XlRange.get worksheet rangeString
            Array.toRange rng (Utils.fields x)
        )
        XlWorkbook.saveAs workbook path
        XlApp.quit xl