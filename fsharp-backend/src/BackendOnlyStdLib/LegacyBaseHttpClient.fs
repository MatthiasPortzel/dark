module BackendOnlyStdLib.LegacyBaseHttpClient

open System.IO
open System.IO.Compression
open System.Net.Http
open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open LibExecution
open LibBackend
open LibExecution.RuntimeTypes
open LibExecution.VendoredTablecloth

type AspHeaders = System.Net.Http.Headers.HttpHeaders

module DvalRepr = LibExecution.DvalReprExternal
module Errors = LibExecution.Errors
module RT = RuntimeTypes

let incorrectArgs = Errors.incorrectArgs

module MediaType =
  type T =
    | Form
    | Xml
    | Json
    | Text
    | Html
    | Other of string

    override this.ToString() : string =
      match this with
      | Form -> "application/x-www-form-urlencoded"
      | Xml -> "application/xml"
      | Json -> "application/json"
      | Text -> "text/plain"
      | Html -> "text/html"
      | Other s -> s

  let parse (str : string) : T =
    match String.trim str with
    | "application/x-www-form-urlencoded" -> Form
    | "application/xml" -> Xml
    | "application/json" -> Json
    | "text/plain" -> Text
    | "text/html" -> Html
    | _ -> Other str

module Charset =
  type T =
    | Utf8
    | NotUtf8 of string

    override this.ToString() : string =
      match this with
      | Utf8 -> "utf-8"
      | NotUtf8 s -> s

  let parse (str : string) : T =
    match String.trim str with
    | "utf-8" -> Utf8
    | _ -> NotUtf8 str

module ContentType =
  type T =
    | Known of MediaType.T * Charset.T
    | KnownNoCharset of MediaType.T
    | Unknown of string // don't parse out charset or boundary or anything

    override this.ToString() : string =
      match this with
      | Known (mt, cs) -> $"{mt}; charset={cs}"
      | KnownNoCharset (mt) -> string mt
      | Unknown s -> s

  let toMediaType (ct : T) : Option<MediaType.T> =
    match ct with
    | Known (mt, _) -> Some mt
    | KnownNoCharset (mt) -> Some mt
    | Unknown s -> None

  let toHttpHeader (ct : T) : HttpHeaders.Header = "Content-Type", string ct

  let parse (str : string) : T =
    match String.split ";" str |> List.map String.trim with
    | [ mt; cs ] ->
      match String.split "=" cs |> List.map String.trim with
      | [ "charset"; cs ] -> Known(MediaType.parse mt, Charset.parse cs)
      | _ -> Unknown(str)
    | [ mt ] -> KnownNoCharset(MediaType.parse mt)
    | _ -> Unknown str

  let text = (Known(MediaType.Text, Charset.Utf8))
  let json = (Known(MediaType.Json, Charset.Utf8))

  let textHeader : HttpHeaders.Header = toHttpHeader text

  let getContentType (headers : HttpHeaders.T) : Option<T> =
    headers |> HttpHeaders.get "Content-type" |> Option.map parse

  let getMediaType (headers : HttpHeaders.T) : Option<MediaType.T> =
    headers |> getContentType |> Option.bind toMediaType

  let hasNoContentType (headers : HttpHeaders.T) : bool =
    headers |> getContentType |> Option.isNone

  let hasJsonHeader (headers : HttpHeaders.T) : bool =
    // CLEANUP: don't use contains for this
    HttpHeaders.get "content-type" headers
    |> Option.map (fun s -> s.Contains "application/json")
    |> Option.defaultValue false

  // this isn't a "contains", to match the OCaml impl.
  let hasFormHeaderWithoutCharset (headers : HttpHeaders.T) : bool =
    HttpHeaders.get "content-type" headers
    |> Option.map (fun s -> s = "application/x-www-form-urlencoded")
    |> Option.defaultValue false


type headers = (string * string) list

// includes an implicit content-type
type Content =
  // OCaml's impl. uses cURL under the hood.
  // cURL is special_ in that it will assume that
  // the request is a _form_ request if unspecified,
  // when POST/PUTing
  | FakeFormContentToMatchCurl of string
  | FormContent of string
  | StringContent of string
  | NoContent

// There has been quite a history of HTTPClient having problems in previous versions
// of .NET, including socket exhaustion and DNS results not expiring. The history is
// handled quite well in
// https://www.stevejgordon.co.uk/httpclient-connection-pooling-in-dotnet-core
//
// As of today (using .NET6) it seems we no longer need to worry about either socket
// exhaustion or DNS issues. It appears that we can use either multiple HTTP clients
// or just one, we use just one for efficiency.
// See https://docs.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-6.0#alternatives-to-ihttpclientfactory
//
// Note that I manually verified by hand the number of sockets, which you can do with
//   sudo netstat -apn | grep _WAIT
let socketHandler : HttpMessageHandler =
  let handler = new SocketsHttpHandler()

  // Avoid DNS problems
  handler.PooledConnectionIdleTimeout <- System.TimeSpan.FromMinutes 5.0
  handler.PooledConnectionLifetime <- System.TimeSpan.FromMinutes 10.0

  // Note, do not do automatic decompression, see decompression code later for details
  handler.AutomaticDecompression <- System.Net.DecompressionMethods.None

  // If we use auto-redirect, we can't limit the protocols or massage the headers, so
  // we're going to have to implement this manually
  handler.AllowAutoRedirect <- false

  // CLEANUP add port into config var
  // This port is assumed by Curl in the OCaml version, but not by .NET
  handler.UseProxy <- true
  handler.Proxy <- System.Net.WebProxy(Config.httpclientProxyUrl, false)

  // Users share the HttpClient, don't let them share cookies!
  handler.UseCookies <- false
  handler :> HttpMessageHandler


let httpClient : HttpClient =
  let client = new HttpClient(socketHandler, disposeHandler = false)
  client.Timeout <- System.TimeSpan.FromSeconds 30.0
  // Can't find what this was in OCaml/Curl, but 100MB seems a reasonable default
  client.MaxResponseContentBufferSize <- 1024L * 1024L * 100L
  client

type HttpResult = { body : string; code : int; headers : HttpHeaders.T }

type ClientError = { url : string; error : string; code : int }

// -------------------------
// Forms and queries Functions
// -------------------------

// Convert .NET HttpHeaders into Dark-style headers
let convertHeaders (headers : AspHeaders) : HttpHeaders.T =
  headers
  |> Seq.map
    (fun (kvp : System.Collections.Generic.KeyValuePair<string, seq<string>>) ->
      (kvp.Key, kvp.Value |> Seq.toList |> String.concat ","))
  |> Seq.toList

exception InvalidEncodingException of int


let prependInternalErrorMessage errorMessage =
  $"Internal HTTP-stack exception: {errorMessage}"

let makeHttpCall
  (rawBytes : bool)
  (url : string)
  (queryParams : (string * string list) list)
  (method : HttpMethod)
  (reqHeaders : HttpHeaders.T)
  (reqBody : Content)
  : Task<Result<HttpResult, ClientError>> =
  task {
    try
      let uri = System.Uri(url, System.UriKind.Absolute)
      if uri.Scheme <> "https" && uri.Scheme <> "http" then
        return
          Error
            { url = url
              code = 0
              error = prependInternalErrorMessage "Unsupported protocol" }
      else
        // Remove the parts of the existing Uri that are duplicated or handled in
        // other ways
        let reqUri = System.UriBuilder()
        reqUri.Scheme <- uri.Scheme
        reqUri.Host <- uri.Host
        reqUri.Port <- uri.Port
        reqUri.Path <- uri.AbsolutePath
        let queryString =
          // Remove leading '?'
          if uri.Query = "" then "" else uri.Query.Substring 1
        reqUri.Query <-
          DvalRepr.queryToEncodedString (
            queryParams @ DvalRepr.parseQueryString queryString
          )
        use req = new HttpRequestMessage(method, string reqUri)

        // CLEANUP We could use Http3. This uses Http2 as that's what was supported in
        // OCaml/Curl, and we don't want to change behaviour. The potential behaviour
        // is that we know the behaviour of headers in Http2 (our OCaml code lowercases
        // them in Http2 only, but we don't want a surprise with Http3 when they're
        // dynamically upgraded)
        req.Version <- System.Net.HttpVersion.Version20

        // username and password - note that an actual auth header will overwrite this
        if uri.UserInfo <> "" then
          let authString =
            // UserInfo is escaped during parsing, but shouldn't actually isn't
            // useful here, so unescape it.
            let userInfo = System.Uri.UnescapeDataString uri.UserInfo
            // Handle usernames with no colon
            if userInfo.Contains(":") then userInfo else userInfo + ":"
          req.Headers.Authorization <-
            Headers.AuthenticationHeaderValue(
              "Basic",
              System.Convert.ToBase64String(UTF8.toBytes authString)
            )

        // content
        let utf8 = System.Text.Encoding.UTF8
        match reqBody with
        | FormContent s ->
          req.Content <-
            new StringContent(s, utf8, "application/x-www-form-urlencoded")
        | StringContent str ->
          req.Content <- new StringContent(str, utf8, "text/plain")
        | NoContent -> req.Content <- new ByteArrayContent [||]
        | FakeFormContentToMatchCurl s ->
          req.Content <-
            new StringContent(s, utf8, "application/x-www-form-urlencoded")
          req.Content.Headers.ContentType.CharSet <- System.String.Empty

        // headers
        let defaultHeaders =
          [ "Accept", "*/*"; "Accept-Encoding", "deflate, gzip, br" ] |> Map

        Map reqHeaders
        |> Map.mergeFavoringRight defaultHeaders
        |> Map.iter (fun k v ->
          if v = "" then
            // CLEANUP: OCaml doesn't send empty headers, but no reason not to
            ()
          elif String.equalsCaseInsensitive k "content-type" then
            req.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse(v)
          else
            // Dark headers can only be added once, as they use a Dict. Remove them
            // so they don't get added twice (eg via Authorization headers above)
            req.Headers.Remove(k) |> ignore<bool>
            let added = req.Headers.TryAddWithoutValidation(k, v)
            // Headers are split between req.Headers and req.Content.Headers so just try both
            if not added then
              req.Content.Headers.Remove(k) |> ignore<bool>
              req.Content.Headers.Add(k, v))

        // send request
        use! response = httpClient.SendAsync req

        // We do not do automatic decompression, because if we did, we would lose the
        // content-Encoding header, which the automatic decompression removes for
        // some reason.
        // From http://www.west-wind.com/WebLog/posts/102969.aspx
        let encoding = response.Content.Headers.ContentEncoding.ToString()
        use! responseStream = response.Content.ReadAsStreamAsync()
        use contentStream =
          let decompress = CompressionMode.Decompress
          // The version of Curl we used in OCaml does not support zstd, so omitting
          // that won't break anything.
          match String.toLowercase encoding with
          | "br" -> new BrotliStream(responseStream, decompress) :> Stream
          | "gzip" -> new GZipStream(responseStream, decompress) :> Stream
          | "deflate" -> new DeflateStream(responseStream, decompress) :> Stream
          | "" -> responseStream
          | _ -> raise (InvalidEncodingException(int response.StatusCode))

        use memoryStream = new MemoryStream()
        do! contentStream.CopyToAsync(memoryStream)
        let respBody = memoryStream.ToArray()

        let respString =
          // CLEANUP we can support any encoding that .NET supports, which I bet is a
          // lot
          let latin1 =
            try
              let charset = response.Content.Headers.ContentType.CharSet
              match charset with
              | "latin1"
              | "us-ascii"
              | "iso-8859-1"
              | "iso_8859-1" -> true
              | _ -> false
            with
            | _ -> false
          if latin1 then
            System.Text.Encoding.Latin1.GetString respBody
          else
            // CLEANUP there are other options here, and this is a bad error message
            UTF8.ofBytesOpt respBody |> Option.defaultValue "utf-8 decoding error"

        let code = int response.StatusCode

        let isHttp2 = (response.Version = System.Net.HttpVersion.Version20)

        // CLEANUP: For some reason, the OCaml version includes a header with the HTTP
        // status line the response and each redirect.
        let statusHeader =
          if isHttp2 then
            $"HTTP/2 {code}"
          else
            $"HTTP/{response.Version} {code} {response.ReasonPhrase}"

        let headers =
          convertHeaders response.Headers @ convertHeaders response.Content.Headers

        // CLEANUP The OCaml version automatically made this lowercase for
        // http2. That's a weird experience for users, as they don't have
        // control over this, so make this lowercase by default
        let headers =
          if isHttp2 then
            List.map (fun (k : string, v) -> (String.toLowercase k, v)) headers
          else
            headers

        let result =
          { body = respString
            code = code
            headers = [ statusHeader, "" ] @ headers }
        return Ok result
    with
    | InvalidEncodingException code ->
      let error = "Unrecognized or bad HTTP Content or Transfer-Encoding"
      return
        Error { url = url; code = code; error = prependInternalErrorMessage error }
    | :? TaskCanceledException -> // only timeouts
      return
        Error { url = url; code = 0; error = prependInternalErrorMessage "Timeout" }
    | :? System.ArgumentException as e -> // incorrect protocol, possibly more
      let message =
        if e.Message = "Only 'http' and 'https' schemes are allowed. (Parameter 'value')" then
          "Unsupported protocol"
        else
          e.Message
      return
        Error { url = url; code = 0; error = prependInternalErrorMessage message }
    | :? System.UriFormatException ->
      return Error { url = url; code = 0; error = "Invalid URI" }
    | :? IOException as e ->
      return
        Error { url = url; code = 0; error = prependInternalErrorMessage e.Message }
    | :? HttpRequestException as e ->
      let code = if e.StatusCode.HasValue then int e.StatusCode.Value else 0
      return
        Error
          { url = url; code = code; error = prependInternalErrorMessage e.Message }
  }
