/// Railway-oriented programming infrastructure.
/// All domain errors are modelled as discriminated unions so callers pattern-match
/// exhaustively rather than catching exceptions.  AppError is the single top-level
/// error type that flows on the Error rail throughout the entire application.
module AgentCore.Core.Error

// ---------------------------------------------------------------------------
// Domain error hierarchy
// ---------------------------------------------------------------------------

type ConfigError =
    | FileNotFound      of path: string
    | ParseFailure      of message: string
    | MissingProvider   of name: string
    | UnresolvedEnvVar  of varName: string

type ProviderError =
    | HttpError          of statusCode: int * body: string
    | NetworkFailure     of message: string
    | ResponseParseError of message: string
    | AuthenticationError
    | ModelNotFound      of modelName: string
    | ToolsNotSupported  of providerName: string

type ToolError =
    | ToolNotFound      of name: string
    | InvalidArguments  of toolName: string * message: string
    | ExecutionFailed   of toolName: string * message: string

/// Single top-level error union that rides the Error rail across all layers.
/// Wrapping sub-errors means callers can handle at whatever granularity they need.
type AppError =
    | ConfigErr   of ConfigError
    | ProviderErr of ProviderError
    | ToolErr     of ToolError
    | MaxTurnsExceeded
    | Unexpected  of message: string

// Convenience lifts ---------------------------------------------------------

let configErr   e = Error (ConfigErr e)
let providerErr e = Error (ProviderErr e)
let toolErr     e = Error (ToolErr e)

// ---------------------------------------------------------------------------
// AsyncResult<'a> — the core monadic type
// ---------------------------------------------------------------------------

/// Async<Result<'a, AppError>> — the primary effect type in this codebase.
/// Every effectful operation that can fail returns this type so that
/// async I/O and error-handling compose cleanly via bind.
type AsyncResult<'a> = Async<Result<'a, AppError>>

// ---------------------------------------------------------------------------
// Result module extensions (pure rail)
// ---------------------------------------------------------------------------

module Result =

    /// Apply a function inside Ok, leave Error unchanged.
    let mapErr f = function Ok v -> Ok v | Error e -> Error (f e)

    /// Kleisli composition: f >=> g  (left-to-right bind)
    let composeK f g x = f x |> Result.bind g

    /// Convert Option to Result with a supplied error.
    let ofOption err = function
        | Some v -> Ok v
        | None   -> Error err

    /// Collapse a list of Results into Result<list, error>, stopping at first Error.
    let sequence (results: Result<'a, 'e> list) : Result<'a list, 'e> =
        List.foldBack (fun r acc ->
            match r, acc with
            | Ok v,    Ok vs   -> Ok (v :: vs)
            | Error e, _       -> Error e
            | _,       Error e -> Error e
        ) results (Ok [])

    /// Map each element, collecting results; stop on first Error.
    let traverse f xs = xs |> List.map f |> sequence

// ---------------------------------------------------------------------------
// AsyncResult module — async + result monad operations
// ---------------------------------------------------------------------------

module AsyncResult =

    let ok  (v: 'a)         : AsyncResult<'a> = async { return Ok v }
    let err (e: AppError)   : AsyncResult<'a> = async { return Error e }

    let ofResult (r: Result<'a, AppError>) : AsyncResult<'a> = async { return r }
    let ofAsync  (a: Async<'a>)            : AsyncResult<'a> = async {
        let! v = a
        return Ok v
    }

    let map (f: 'a -> 'b) (x: AsyncResult<'a>) : AsyncResult<'b> = async {
        let! r = x
        return Result.map f r
    }

    let mapError (f: AppError -> AppError) (x: AsyncResult<'a>) : AsyncResult<'a> = async {
        let! r = x
        return Result.mapError f r
    }

    let bind (f: 'a -> AsyncResult<'b>) (x: AsyncResult<'a>) : AsyncResult<'b> = async {
        let! r = x
        match r with
        | Ok v    -> return! f v
        | Error e -> return Error e
    }

    let bindResult (f: 'a -> Result<'b, AppError>) (x: AsyncResult<'a>) : AsyncResult<'b> = async {
        let! r = x
        return Result.bind f r
    }

    /// Apply an async function, lifting any exception into Error.
    let protect (f: 'a -> Async<'b>) (errWrap: string -> AppError) (v: 'a) : AsyncResult<'b> = async {
        try
            let! r = f v
            return Ok r
        with ex ->
            return Error (errWrap ex.Message)
    }

    /// Sequence a list of AsyncResults into AsyncResult<list>.
    let sequence (xs: AsyncResult<'a> list) : AsyncResult<'a list> = async {
        let! results = Async.Sequential xs
        return Result.sequence (Array.toList results)
    }

    let traverse (f: 'a -> AsyncResult<'b>) (xs: 'a list) : AsyncResult<'b list> =
        xs |> List.map f |> sequence

    /// Ignore the Ok value (unit-returning pipelines).
    let ignore (x: AsyncResult<'a>) : AsyncResult<unit> = map ignore x

// ---------------------------------------------------------------------------
// AsyncResult computation expression
// ---------------------------------------------------------------------------

type AsyncResultBuilder() =
    member _.Return(v)             : AsyncResult<'a>  = AsyncResult.ok v
    member _.ReturnFrom(x)         : AsyncResult<'a>  = x
    member _.Zero()                : AsyncResult<unit> = AsyncResult.ok ()
    member _.Delay(f: unit -> AsyncResult<'a>)        = f
    member _.Run(f: unit -> AsyncResult<'a>)          = f ()

    member _.Bind(x: AsyncResult<'a>, f: 'a -> AsyncResult<'b>) : AsyncResult<'b> =
        AsyncResult.bind f x

    member _.Bind(x: Result<'a, AppError>, f: 'a -> AsyncResult<'b>) : AsyncResult<'b> =
        AsyncResult.bind f (AsyncResult.ofResult x)

    member this.Combine(a: AsyncResult<unit>, b: unit -> AsyncResult<'b>) : AsyncResult<'b> =
        this.Bind(a, fun () -> b ())

    member _.TryWith(body: unit -> AsyncResult<'a>, handler: exn -> AsyncResult<'a>) : AsyncResult<'a> =
        async {
            try return! body ()
            with ex -> return! handler ex
        }

    member _.TryFinally(body: unit -> AsyncResult<'a>, fin: unit -> unit) : AsyncResult<'a> =
        async {
            try   return! body ()
            finally fin ()
        }

    member _.Using(resource: 'r when 'r :> System.IDisposable, body: 'r -> AsyncResult<'a>) : AsyncResult<'a> =
        async {
            use r = resource
            return! body r
        }

    member this.While(guard: unit -> bool, body: unit -> AsyncResult<unit>) : AsyncResult<unit> =
        if guard ()
        then this.Bind(body (), fun () -> this.While(guard, body))
        else this.Zero()

    member this.For(xs: 'a seq, body: 'a -> AsyncResult<unit>) : AsyncResult<unit> =
        this.Using(xs.GetEnumerator(), fun e ->
            this.While(e.MoveNext, fun () -> body e.Current))

/// The global computation expression builder — used everywhere as `asyncResult { ... }`.
let asyncResult = AsyncResultBuilder()

// ---------------------------------------------------------------------------
// Result computation expression  (pure, synchronous rail)
// ---------------------------------------------------------------------------

type ResultBuilder() =
    member _.Return(v)                            = Ok v
    member _.ReturnFrom(r: Result<'a,'e>)         = r
    member _.Zero()                               = Ok ()
    member _.Delay(f: unit -> Result<'a,'e>)      = f
    member _.Run(f: unit -> Result<'a,'e>)        = f ()
    member _.Bind(r, f)                           = Result.bind f r
    member this.Combine(a, b)                     = this.Bind(a, fun () -> b ())
    member _.TryWith(b, h)                        = try b () with ex -> h ex
    member _.TryFinally(b, fin)                   = try b () finally fin ()

/// Synchronous result CE — used for pure Result chains as `result { ... }`.
let result = ResultBuilder()

// ---------------------------------------------------------------------------
// Operator aliases for fluent pipeline style
// ---------------------------------------------------------------------------

/// Kleisli composition (left-to-right) for AsyncResult.
let (>=>) (f: 'a -> AsyncResult<'b>) (g: 'b -> AsyncResult<'c>) : 'a -> AsyncResult<'c> =
    fun x -> AsyncResult.bind g (f x)
