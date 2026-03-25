/// Entry point — registers CLI commands and routes execution.
module AgentResearch.Program

open Spectre.Console.Cli
open AgentResearch.CLI.Commands

[<EntryPoint>]
let main argv =
    let app = CommandApp<ResearchCommand>()
    app.Configure(fun config ->
        config.SetApplicationName("fsharp-research-agent")
        config.AddCommand<ResearchCommand>("research")
            .WithDescription("Run a multi-step research agent on a question")
            .WithExample([| "research"; "What are the key differences between F# and Haskell?" |])
        |> ignore)
    app.Run(argv)
