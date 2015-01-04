﻿module XTract.Settings

type XTractSettings private () =

    static let mutable driverDir : string option = None

    /// The directory containing ChromeDriver.exe.
    static member chromeDriverDirectory 
        with get () = Option.get driverDir
        and set dir = driverDir <- Some dir