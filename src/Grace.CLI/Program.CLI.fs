namespace Grace.Cli

open Grace.Cli.Command
open Grace.Cli.Common
open Grace.Cli.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Converters
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open NodaTime.Text
open Spectre.Console
open System
open System.Collections
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Builder
open System.CommandLine.Help
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Diagnostics
open System.Globalization
open System.IO
open System.Linq
open System.Threading.Tasks
open Grace.Shared.Validation

module Configuration =
    
    type GraceCLIConfiguration =
        {
            GraceWatchStatus: GraceWatchStatus
        }

    let mutable private cliConfiguration = {GraceWatchStatus = GraceWatchStatus.Default}
    let CLIConfiguration() = cliConfiguration
    let updateConfiguration (config: GraceCLIConfiguration) =
        cliConfiguration <- config

module GraceCommand =

    type OptionToUpdate = {optionName: string; displayValue: string}

    /// Built-in aliases for Grace commands.
    let private aliases = 
        let aliases = Dictionary<string, string seq>()
        aliases.Add("branches", ["repository"; "get-branches"])
        aliases.Add("checkpoint", ["branch"; "checkpoint"])
        aliases.Add("checkpoints", ["branch"; "get-checkpoints"])
        aliases.Add("commit", ["branch"; "commit"])
        aliases.Add("commits", ["branch"; "get-commits"])
        aliases.Add("promote", ["branch"; "promote"])
        aliases.Add("promotions", ["branch"; "get-promotions"])
        aliases.Add("rebase", ["branch"; "rebase"])
        aliases.Add("refs", ["branch"; "get-references"])
        aliases.Add("save", ["branch"; "save"])
        aliases.Add("saves", ["branch"; "get-references"])
        aliases.Add("status", ["branch"; "status"])
        aliases.Add("switch", ["branch"; "switch"])
        aliases.Add("tag", ["branch"; "tag"])
        aliases.Add("tags", ["branch"; "get-tags"])
        //aliases.Add("", [""; ""])
        aliases

    /// The character sequences that Grace will recognize as a request for help.
    let helpOptions = [| "-h"; "/h"; "--help"; "-?"; "/?" |]
        
    let Build =
        // Create the root of the command tree.
        let rootCommand = new RootCommand("Grace Version Control System", Name = "grace")

        // Turning this off means getting much more flexible in our input handling, and that's not happening for a while.
        rootCommand.TreatUnmatchedTokensAsErrors <- true

        // Create global options - these appear on every command in the system.
        rootCommand.AddGlobalOption(Options.correlationId)
        rootCommand.AddGlobalOption(Options.output)

        // Add subcommands.
        rootCommand.AddCommand(Connect.Build)
        rootCommand.AddCommand(Watch.Build)
        rootCommand.AddCommand(Branch.Build)
        rootCommand.AddCommand(Diff.Build)
        rootCommand.AddCommand(Repository.Build)
        rootCommand.AddCommand(Organization.Build)
        rootCommand.AddCommand(Owner.Build)
        rootCommand.AddCommand(Config.Build)
        rootCommand.AddCommand(Maintenance.Build)

        /// The feedback section is printed at the end of any CLI request for help.
        let feedbackSection: HelpSectionDelegate = HelpSectionDelegate(fun context -> 
            context.Output.WriteLine()
            context.Output.WriteLine("More help and feedback:")
            context.Output.WriteLine("  For more help, or to give us feedback, please create an issue in our repo at https://github.com/scottarbeit/grace."))

        /// Handles command aliases by removing the alias and substituting the full Grace command before execution.
        let aliasHandler (context: InvocationContext) =
            let tokens = context.ParseResult.Tokens.Select(fun token -> token.Value).ToList()
            if tokens.Count > 0 then
                let firstToken = tokens[0]
                if aliases.ContainsKey(firstToken) then
                    tokens.RemoveAt(0)
                    // Reverse the full command so we insert them in the right order.
                    for token in aliases[firstToken].Reverse() do
                        tokens.Insert(0, token)
                context.ParseResult <- context.Parser.Parse(tokens)

        // Build the whole thing into a command system.        
        let commandLineBuilder = CommandLineBuilder(rootCommand)
        commandLineBuilder
            .UseHelp(fun helpContext ->
                // This is where we configure how help is displayed by Grace.

                // This checks to see if help was requested at the parent `grace` level, or if no commands or parameters were specified.
                // If so, we'll show the Figlet text and top-level commands and options.
                if helpContext.ParseResult.Tokens.Count = 0 || 
                        (helpContext.ParseResult.Tokens.Count = 1 && helpOptions.Contains(helpContext.ParseResult.Tokens[0].Value)) then
                    let graceFiglet = FigletText("Grace Version Control System")
                    graceFiglet.Color <- Color.Green3
                    graceFiglet.Justification <- Justify.Center
                    AnsiConsole.Write(graceFiglet)
                    AnsiConsole.WriteLine()

                    // Skip the description section if we printed FigletText.
                    helpContext.HelpBuilder.CustomizeLayout(fun layoutContext ->
                        HelpBuilder.Default.GetLayout().Where(fun section -> not <| section.Method.Name.Contains("Synopsis", StringComparison.InvariantCultureIgnoreCase))
                    )

                let rec gatherAllOptions (command: Command) (allOptions: List<Option>) = 
                    allOptions.AddRange(command.Options)
                    let parentCommand = command.Parents.OfType<Command>().FirstOrDefault()
                    if not <| isNull parentCommand then
                        gatherAllOptions parentCommand allOptions
                    else
                        allOptions

                let allOptions = gatherAllOptions helpContext.Command (List<Option>())

                //logToConsole "Children:"
                //for xx in helpContext.Command.Children do
                //    logToConsole $"{xx.Name}; {xx.Description}"

                //AnsiConsole.WriteLine()
                //logToConsole "All options:"
                //for xx in allOptions do
                //    logToConsole $"{xx.Name}; {xx.Description}"

                // This section sets the display of the default value for these options in all commands in Grace CLI.
                // Without setting it here, by default, we'd get something like "[default: thing-we-said-in-the-Option-definition] [default:e4def31b-4547-4f6b-9324-56eba666b4b2]" i.e. whatever the generated Guid value on create might be.
                let optionsToUpdate = [
                    {optionName = "correlationId"; displayValue = "new Guid"}
                    {optionName = "branchId"; displayValue = "current branch, or new Guid on create"}
                    {optionName = "organizationId"; displayValue = "current organization, or new Guid on create"}
                    {optionName = "ownerId"; displayValue = "current owner, or new Guid on create"}
                    {optionName = "repositoryId"; displayValue = "current repository, or new Guid on create"}
                    {optionName = "parentBranchId"; displayValue = "current branch, empty if parentBranchName is provided"}
                ]

                for optionToUpdate in optionsToUpdate do
                    for option in allOptions.Where(fun opt -> opt.Name = optionToUpdate.optionName) do
                        helpContext.HelpBuilder.CustomizeSymbol(option, defaultValue = optionToUpdate.displayValue)

                // Add the feedback section at the end.
                helpContext.HelpBuilder.CustomizeLayout(fun layoutContext ->
                    HelpBuilder.Default.GetLayout().Append(feedbackSection)
                )
            )
            .UseDefaults()
            .RegisterWithDotnetSuggest()
            .AddMiddleware(aliasHandler, MiddlewareOrder.ExceptionHandler)
            .UseParseErrorReporting()
            .Build()

    let isGraceWatch (parseResult: ParseResult) =
        if (parseResult.CommandResult.Command.Name = "watch") then true else false

    let isGraceConfig (parseResult: ParseResult) =
        if not <| isNull parseResult.CommandResult.Parent then
            if (parseResult.CommandResult.Parent.Symbol.Name = "config") then true else false
        else
            false

    [<EntryPoint>]
    let main args =
        let startTime = getCurrentInstant()
        (task {
            let mutable parseResult: ParseResult = null
            let mutable returnValue: int = 0
            try
                try
                    //use logListener = new TextWriterTraceListener(Path.Combine(Current().ConfigurationDirectory, "grace.log"))
                    //Threading.ThreadPool.SetMinThreads(Constants.ParallelOptions.MaxDegreeOfParallelism, Constants.ParallelOptions.MaxDegreeOfParallelism) |> ignore
                    let command = Build

                    //let n = Constants.JsonSerializerOptions.Converters.Count
                    //Constants.JsonSerializerOptions.Converters.Add(BranchDtoConverter())
                    //logToAnsiConsole Colors.Important $"Was {n}, now {Constants.JsonSerializerOptions.Converters.Count}."

                    if configurationFileExists() then
                        parseResult <- command.Parse(args)
                        if parseResult |> showOutput then
                            if parseResult |> verbose then
                                AnsiConsole.Write((new Rule($"[{Colors.Important}]Started: {startTime.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}.[/]")).RightJustified())
                            else
                                AnsiConsole.Write(new Rule())

                        if not <| (parseResult |> isGraceWatch) then
                            let! graceWatchStatus = getGraceWatchStatus()
                            match graceWatchStatus with
                            | Some status ->
                                //logToAnsiConsole Colors.Verbose $"Opened IPC file."
                                Configuration.updateConfiguration {GraceWatchStatus = status}
                            | None ->
                                    //logToAnsiConsole Colors.Verbose $"Couldn't open IPC file."
                                    ()

                        logToConsole (parseResult |> isGraceConfig)
                        let! returnValue = command.InvokeAsync(args)
                        ()

                        // We'll tell the user now, before the Rule(), but we actually delete the inter-process communication file in the finally clause.
                        if parseResult |> isGraceWatch then
                            logToAnsiConsole Colors.Important $"Inter-process communication file deleted."

                        if parseResult |> showOutput then
                            let finishTime = getCurrentInstant()
                            let elapsed = finishTime - startTime
                            if parseResult |> verbose then
                                AnsiConsole.Write((new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}. Finished: {finishTime.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}[/]")).RightJustified())
                            else
                                AnsiConsole.Write((new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}.[/]")).RightJustified())

                            AnsiConsole.WriteLine()
                    else
                        AnsiConsole.Write(new Rule())

                        if args.Any(fun arg -> arg = "config") then
                            let! returnValue = command.InvokeAsync(args)
                            ()
                        else
                            printfn $"No {Constants.GraceConfigFileName} file found along current path. Please run `grace config write` to create one."
                        printParseResult parseResult
                        let finishTime = getCurrentInstant()
                        let elapsed = finishTime - startTime
                        AnsiConsole.Write((new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}.[/]")).RightJustified())

                    return returnValue
                with ex ->
                    logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}"
                    logToAnsiConsole Colors.Error $"{ex.StackTrace}"
                    return -1
            finally
                // If this was grace watch, delete the inter-process communication file.
                if not <| isNull(parseResult) && parseResult |> isGraceWatch then
                    File.Delete(IpcFileName())
        }).Result
