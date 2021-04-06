module ApiServer.Secrets

// API endpoints for Secrets

open Microsoft.AspNetCore.Http
open Giraffe

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Tablecloth

module PT = LibBackend.ProgramTypes
module OT = LibBackend.OCamlInterop.OCamlTypes
module ORT = LibBackend.OCamlInterop.OCamlTypes.RuntimeT
module AT = LibExecution.AnalysisTypes
module Convert = LibBackend.OCamlInterop.Convert

type Secret = { secret_name : string; secret_value : string }

type Params = Secret

type T = { secrets : List<Secret> }

let insertSecret (ctx : HttpContext) : Task<T> =
  task {
    try
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      let! p = ctx.BindModelAsync<Params>()
      t "read-api"

      do! LibBackend.Secret.insert canvasInfo.id p.secret_name p.secret_value
      t "insert-secret"

      let! secrets = LibBackend.Secret.getCanvasSecrets canvasInfo.id
      t "get-secrets"

      return
        { secrets =
            List.map
              (fun (s : LibBackend.Secret.Secret) ->
                { secret_name = s.name; secret_value = s.value })
              secrets }

    with e ->
      let msg = e.ToString()

      if String.includes "duplicate key value violates unique constraint" msg then
        failwith "The secret's name is already defined for this canvas"
      else
        raise e

      return { secrets = [] }
  }
