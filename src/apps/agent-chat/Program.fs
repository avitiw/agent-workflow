/// Entry point — registers CLI commands and routes execution.
module AgentCore.Program

open Spectre.Console.Cli
open AgentCore.CLI.Commands

[<EntryPoint>]
let main argv =
    let app = CommandApp()
    app.Configure(fun config ->
        config.SetApplicationName("fsharp-agent-core")
        config.AddCommand<ChatCommand>("chat")
            .WithDescription("Run the agent with a prompt (or enter interactive mode)")
            .WithExample([| "chat"; "--provider"; "ollama-local"; "What is the capital of France?" |])
        |> ignore
        config.AddCommand<ProvidersCommand>("providers")
            .WithDescription("List all configured providers")
        |> ignore
        config.AddCommand<ModelsCommand>("models")
            .WithDescription("List available models for an Ollama provider")
        |> ignore)
    app.Run(argv)
