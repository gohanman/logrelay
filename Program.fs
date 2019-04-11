
open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Collections.Concurrent
open System.Threading

// from config file "relay_port"
let mutable LOCALPORT = 1514
// from config file "destination_host" and "destination_port"
let mutable RELAYSERVER:(string * int) = ("127.0.0.1", 0)
// connection to RELAYSERVER; global to avoid reconnecting repeatedly
let mutable RELAY:NetworkStream = null
// increment wait between reconnect attempts
let mutable BACKOFF = 1000
let EOL = [| 10uy |]

// Read lines from connection and add to the queue
let acceptClient (client:TcpClient) (queue:BlockingCollection<byte[]>) = async {
   use stream = client.GetStream()
   use reader = new StreamReader(stream, Encoding.ASCII)
   while (not reader.EndOfStream) do
      let line = reader.ReadLine()
      Encoding.ASCII.GetBytes(line) |> queue.Add
      ()
   }

// simple TCP server to queue all incoming data
let startServer (address, port) queue =
   let listener = Sockets.TcpListener(address, port)
   listener.Start() 
   async { 
      while true do 
         let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
         acceptClient client queue |> Async.Start
   }
   |> Async.Start

let reconnect (address, port) =
    (new TcpClient(address, port)).GetStream()

// send msg to RELAYSERVER
let relayMsg msg = 
    try
        RELAY.Write(msg, 0, msg.Length)
        RELAY.Write(EOL, 0, 1)
        BACKOFF <- 1000
    with
    | ex -> (
            printfn "[TRYING TO RECONNECT] %s" (ex.ToString())
            try
                Thread.Sleep(BACKOFF)
                RELAY <- reconnect RELAYSERVER
            with
            | _ -> BACKOFF <- BACKOFF + 1000
        )

// send all queued messages to RELAYSERVER
let rec processQueue (queue:BlockingCollection<byte[]>) =
    queue.Take() |> relayMsg
    processQueue queue

// find config file
let findConf () =
    let here = AppDomain.CurrentDomain.BaseDirectory
    let confs = [ "here" + "logrelay.conf"; "/etc/logrelay.conf" ]
    List.filter File.Exists confs |> List.head

// apply configuration values
let setConf (name, param) =
    match name with
    | "relay_port" -> LOCALPORT <- Int32.Parse(param)
    | "destination_host" -> RELAYSERVER <- (param, snd RELAYSERVER)
    | "destination_port" -> RELAYSERVER <- (fst RELAYSERVER, Int32.Parse(param))
    | _ -> printfn "%s" ("Warning: unknown config option " + name)
    ()

// convert config file to (name, value) tuples
let readConf file =
    File.ReadAllLines(file)
    |> Array.map (fun x -> x.Trim())
    |> Array.filter (fun x -> x.Length > 0)
    |> Array.filter (fun x -> x.[0] <> '#')
    |> Array.filter (fun x -> x.Contains(" ") || x.Contains("\t"))
    |> Array.map (fun x -> x.Split([|' ';'\t'|], 2))
    |> Array.map (fun x -> (x.[0].Trim(), x.[1].Trim()))
    |> Array.iter setConf

[<EntryPoint>]
let main argv =
    printfn "Starting log relay"
    let confFile = 
        try
            findConf ()
        with
        | _ -> (
                printfn "Error: no config file logrelay.conf found"
                printfn "%s" ("Paths checked: /etc, " + AppDomain.CurrentDomain.BaseDirectory)
                Environment.Exit(1)
                ""
            )
    readConf confFile
    let queue = new BlockingCollection<byte[]>()
    RELAY <- reconnect RELAYSERVER
    startServer (IPAddress.Any, 1514) queue
    processQueue queue
    0 // return an integer exit code

