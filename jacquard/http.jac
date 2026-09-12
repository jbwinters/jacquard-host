-- HTTP/1.1 subset: parsing, validation, routing, and response rendering
-- (roadmap H1.1). Everything about HTTP meaning lives here, in Jacquard.
-- The host only frames and bounds raw bytes; each byte arrives as one
-- codepoint 0..255, so text positions are byte positions.
--
-- Accepted: GET and POST, request-target starting with "/", HTTP/1.1,
-- CRLF line endings, one Host header, an optional explicit Content-Length
-- body. Rejected: any other method (501), version (505), bare LF, obsolete
-- line folding, missing or repeated Host, repeated or malformed
-- Content-Length, a body without a length or with the wrong length, a GET
-- with a body, Transfer-Encoding or Upgrade (501), Expect (417), and every
-- size ceiling below (400 or 413).
type HttpMethod = | Get | Post

type HttpHeader = | HttpHeader(name: Text, value: Text)

type HttpRequest = | HttpRequest(method: HttpMethod, target: Text, headers: List HttpHeader, body: Text)

type HttpReject = | HttpReject(status: Int, reason: Text)

type HttpParsed = | HttpOk HttpRequest | HttpBad HttpReject

type HttpResponse = | HttpResponse(status: Int, reason: Text, headers: List HttpHeader, body: Text)

type HttpRoute = | HttpRoute(method: HttpMethod, path: Text)

http.max-head-lines = 32

http.max-line-length = 1024

http.max-target-length = 512

http.max-body-length = 65536

-- ---------------------------------------------------------------- text helpers

http.chars(s) = text.split(s, "")

http.any?(xs, predicate) =
  match xs {
    | Nil -> False
    | Cons(x, rest) -> if predicate(x) then True else http.any?(rest, predicate)
  }

http.all?(xs, predicate) =
  match xs {
    | Nil -> True
    | Cons(x, rest) -> if predicate(x) then http.all?(rest, predicate) else False
  }

http.count(xs, predicate) =
  list.fold(xs, 0, fn (acc, x) -> if predicate(x) then add(acc, 1) else acc)

http.starts-with?(s, prefix) =
  if int.lt?(text.length(s), text.length(prefix)) then False
  else text.eq?(text.slice(s, 0, text.length(prefix)), prefix)

-- split at the first occurrence of a non-empty separator
http.split-once(s, separator) =
  match text.split(s, separator) {
    | Nil -> None
    | Cons(_, Nil) -> None
    | Cons(first, rest) -> Some((first, text.join-list(rest, separator)))
  }

http.lower-pairs =
  [
    ("A", "a"), ("B", "b"), ("C", "c"), ("D", "d"), ("E", "e"), ("F", "f"), ("G", "g"),
    ("H", "h"), ("I", "i"), ("J", "j"), ("K", "k"), ("L", "l"), ("M", "m"), ("N", "n"),
    ("O", "o"), ("P", "p"), ("Q", "q"), ("R", "r"), ("S", "s"), ("T", "t"), ("U", "u"),
    ("V", "v"), ("W", "w"), ("X", "x"), ("Y", "y"), ("Z", "z"),
  ]

http.lower-char(c) =
  match list.find(http.lower-pairs, fn (pair) -> { let (upper, _) = pair
                                                   text.eq?(upper, c) }) {
    | None -> c
    | Some((_, lower)) -> lower
  }

http.lower(s) = text.join-list(list.map(http.chars(s), http.lower-char), "")

http.digits = http.chars("0123456789")

http.digit?(c) = list.contains?(http.digits, c, text.eq)

http.token-chars =
  http.chars("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'*+-.^_`|~")

http.token?(s) =
  if text.empty?(s) then False
  else http.all?(http.chars(s), fn (c) -> list.contains?(http.token-chars, c, text.eq))

-- visible ASCII plus space and horizontal tab
http.field-char?(c) = {
  let code = http.code(c)
  if eq(code, 9) then True
  else if int.lt?(code, 32) then False
  else if eq(code, 127) then False
  else True
}

-- byte value of a singleton text, by position in the table of all bytes
http.byte-table = list.range(0, 256)

http.code(c) =
  match list.find(http.byte-table, fn (n) -> text.eq?(http.byte-text(n), c)) {
    | Some(n) -> n
    | None -> 256
  }

-- the singleton text for a byte value, via the escape table of all bytes
http.byte-text(n) = option.with-default(list.nth(http.all-bytes, n), "")

http.all-bytes = http.chars(http.byte-string)

http.byte-string = "\x00\x01\x02\x03\x04\x05\x06\x07\x08\x09\x0a\x0b\x0c\x0d\x0e\x0f\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1a\x1b\x1c\x1d\x1e\x1f\x20\x21\x22\x23\x24\x25\x26\x27\x28\x29\x2a\x2b\x2c\x2d\x2e\x2f\x30\x31\x32\x33\x34\x35\x36\x37\x38\x39\x3a\x3b\x3c\x3d\x3e\x3f\x40\x41\x42\x43\x44\x45\x46\x47\x48\x49\x4a\x4b\x4c\x4d\x4e\x4f\x50\x51\x52\x53\x54\x55\x56\x57\x58\x59\x5a\x5b\x5c\x5d\x5e\x5f\x60\x61\x62\x63\x64\x65\x66\x67\x68\x69\x6a\x6b\x6c\x6d\x6e\x6f\x70\x71\x72\x73\x74\x75\x76\x77\x78\x79\x7a\x7b\x7c\x7d\x7e\x7f\x80\x81\x82\x83\x84\x85\x86\x87\x88\x89\x8a\x8b\x8c\x8d\x8e\x8f\x90\x91\x92\x93\x94\x95\x96\x97\x98\x99\x9a\x9b\x9c\x9d\x9e\x9f\xa0\xa1\xa2\xa3\xa4\xa5\xa6\xa7\xa8\xa9\xaa\xab\xac\xad\xae\xaf\xb0\xb1\xb2\xb3\xb4\xb5\xb6\xb7\xb8\xb9\xba\xbb\xbc\xbd\xbe\xbf\xc0\xc1\xc2\xc3\xc4\xc5\xc6\xc7\xc8\xc9\xca\xcb\xcc\xcd\xce\xcf\xd0\xd1\xd2\xd3\xd4\xd5\xd6\xd7\xd8\xd9\xda\xdb\xdc\xdd\xde\xdf\xe0\xe1\xe2\xe3\xe4\xe5\xe6\xe7\xe8\xe9\xea\xeb\xec\xed\xee\xef\xf0\xf1\xf2\xf3\xf4\xf5\xf6\xf7\xf8\xf9\xfa\xfb\xfc\xfd\xfe\xff"

-- ------------------------------------------------------------------ parsing

http.reason(status) =
  if eq(status, 200) then "OK"
  else if eq(status, 201) then "Created"
  else if eq(status, 400) then "Bad Request"
  else if eq(status, 404) then "Not Found"
  else if eq(status, 405) then "Method Not Allowed"
  else if eq(status, 409) then "Conflict"
  else if eq(status, 411) then "Length Required"
  else if eq(status, 413) then "Payload Too Large"
  else if eq(status, 417) then "Expectation Failed"
  else if eq(status, 500) then "Internal Server Error"
  else if eq(status, 501) then "Not Implemented"
  else if eq(status, 505) then "HTTP Version Not Supported"
  else "Unknown"

http.bad(status, reason) = HttpBad(HttpReject(status: status, reason: reason))

http.method-of(word) =
  if text.eq?(word, "GET") then Some(Get)
  else if text.eq?(word, "POST") then Some(Post)
  else None

http.parse-request-line(line) =
  match text.split(line, " ") {
    | Cons(method, Cons(target, Cons(version, Nil))) ->
      if bool.not(text.eq?(version, "HTTP/1.1")) then http.bad(505, "only HTTP/1.1 is supported")
      else match http.method-of(method) {
        | None -> http.bad(501, "unsupported method")
        | Some(m) ->
          if text.empty?(target) then http.bad(400, "empty request-target")
          else if bool.not(http.starts-with?(target, "/")) then http.bad(400, "request-target must be origin-form")
          else if int.gt?(text.length(target), http.max-target-length) then http.bad(400, "request-target too long")
          else if bool.not(http.all?(http.chars(target), fn (c) -> bool.and(int.gt?(http.code(c), 32), int.lt?(http.code(c), 127)))) then http.bad(400, "request-target contains an invalid character")
          else HttpOk(HttpRequest(method: m, target: target, headers: [], body: ""))
      }
    | _ -> http.bad(400, "malformed request line")
  }

http.parse-header(line) =
  if bool.or(http.starts-with?(line, " "), http.starts-with?(line, "\t")) then None
  else match http.split-once(line, ":") {
    | None -> None
    | Some((name, rest)) ->
      if bool.not(http.token?(name)) then None
      else {
        let value = text.trim(rest)
        if http.all?(http.chars(value), http.field-char?) then Some(HttpHeader(name: http.lower(name), value: value))
        else None
      }
  }

http.parse-headers(lines, acc) =
  match lines {
    | Nil -> Some(list.reverse(acc))
    | Cons(line, rest) ->
      match http.parse-header(line) {
        | None -> None
        | Some(header) -> http.parse-headers(rest, Cons(header, acc))
      }
  }

http.header-values(headers, name) =
  list.fold(headers, [], fn (acc, header) ->
    match header {
      | HttpHeader(name: n, value: v) -> if text.eq?(n, name) then list.append(acc, [v]) else acc
    })

http.header(request, name) =
  match request {
    | HttpRequest(headers: headers) ->
      match http.header-values(headers, http.lower(name)) {
        | Cons(v, Nil) -> Some(v)
        | _ -> None
      }
  }

http.content-length(values) =
  match values {
    | Nil -> Some(0)
    | Cons(v, Nil) ->
      if text.empty?(v) then None
      else if bool.not(http.all?(http.chars(v), http.digit?)) then None
      else if bool.and(int.gt?(text.length(v), 1), http.starts-with?(v, "0")) then None
      else if int.gt?(text.length(v), 9) then None
      else text.to-int(v)
    | _ -> None
  }

http.validate(request, body) =
  match request {
    | HttpRequest(method: method, target: target, headers: headers) -> {
      let host = http.header-values(headers, "host")
      if bool.not(eq(list.length(host), 1)) then http.bad(400, "exactly one Host header is required")
      else if bool.not(list.empty?(http.header-values(headers, "transfer-encoding"))) then http.bad(501, "transfer codings are not supported")
      else if bool.not(list.empty?(http.header-values(headers, "upgrade"))) then http.bad(501, "protocol upgrade is not supported")
      else if bool.not(list.empty?(http.header-values(headers, "expect"))) then http.bad(417, "expectations are not supported")
      else match http.content-length(http.header-values(headers, "content-length")) {
        | None -> http.bad(400, "malformed or repeated Content-Length")
        | Some(length) ->
          if int.gt?(length, http.max-body-length) then http.bad(413, "body exceeds the accepted size")
          else if bool.not(eq(text.length(body), length)) then http.bad(400, "body length does not match Content-Length")
          else match method {
            | Get -> if int.gt?(length, 0) then http.bad(400, "GET must not carry a body") else HttpOk(HttpRequest(method: method, target: target, headers: headers, body: body))
            | Post -> HttpOk(HttpRequest(method: method, target: target, headers: headers, body: body))
          }
      }
    }
  }

http.parse(raw) =
  match http.split-once(raw, "\r\n\r\n") {
    | None -> http.bad(400, "missing end of head")
    | Some((head, body)) ->
      if text.contains?(head, "\n") then
        if text.contains?(text.join-list(text.split(head, "\r\n"), ""), "\n") then http.bad(400, "bare LF in head") else http.parse-head(head, body)
      else http.parse-head(head, body)
  }

http.parse-head(head, body) =
  match text.split(head, "\r\n") {
    | Nil -> http.bad(400, "empty head")
    | Cons(request-line, header-lines) ->
      if int.gt?(list.length(header-lines), http.max-head-lines) then http.bad(400, "too many header lines")
      else if http.any?(Cons(request-line, header-lines), fn (line) -> int.gt?(text.length(line), http.max-line-length)) then http.bad(400, "line too long")
      else if http.any?(Cons(request-line, header-lines), fn (line) -> text.contains?(line, "\r")) then http.bad(400, "bare CR in head")
      else match http.parse-request-line(request-line) {
        | HttpBad(reject) -> HttpBad(reject)
        | HttpOk(request) ->
          match http.parse-headers(header-lines, []) {
            | None -> http.bad(400, "malformed header line")
            | Some(headers) ->
              match request {
                | HttpRequest(method: method, target: target) ->
                  http.validate(HttpRequest(method: method, target: target, headers: headers, body: ""), body)
              }
          }
      }
  }

-- ------------------------------------------------------------------ routing

http.path(target) =
  match http.split-once(target, "?") {
    | None -> target
    | Some((path, _)) -> path
  }

http.query(target) =
  match http.split-once(target, "?") {
    | None -> ""
    | Some((_, query)) -> query
  }

http.method-eq?(a, b) =
  match (a, b) {
    | (Get, Get) -> True
    | (Post, Post) -> True
    | _ -> False
  }

http.matches?(request, route) =
  match (request, route) {
    | (HttpRequest(method: m, target: target), HttpRoute(method: rm, path: path)) ->
      bool.and(http.method-eq?(m, rm), text.eq?(http.path(target), path))
  }

-- the first route whose method and path match; the caller applies the handler
http.route(request, routes) =
  match list.find(routes, fn (entry) -> { let (route, _) = entry
                                          http.matches?(request, route) }) {
    | None -> None
    | Some((_, handler)) -> Some(handler)
  }

-- a path known under another method distinguishes 405 from 404
http.path-known?(request, routes) =
  http.any?(routes, fn (entry) -> { let (route, _) = entry
                                     match (request, route) {
                                       | (HttpRequest(target: target), HttpRoute(path: path)) -> text.eq?(http.path(target), path)
                                     } })

-- ---------------------------------------------------------------- rendering

http.response(status, headers, body) =
  HttpResponse(status: status, reason: http.reason(status), headers: headers, body: body)

http.text-response(status, body) =
  http.response(status, [HttpHeader(name: "Content-Type", value: "text/plain; charset=utf-8")], body)

http.reject-response(reject) =
  match reject {
    | HttpReject(status: status, reason: reason) -> http.text-response(status, reason)
  }

http.render-header(header) =
  match header {
    | HttpHeader(name: name, value: value) -> text.join-list([name, ": ", value, "\r\n"], "")
  }

http.render(response) =
  match response {
    | HttpResponse(status: status, reason: reason, headers: headers, body: body) ->
      text.join-list(
        [
          "HTTP/1.1 ", text.from-int(status), " ", reason, "\r\n",
          text.join-list(list.map(headers, http.render-header), ""),
          "Content-Length: ", text.from-int(text.length(body)), "\r\n",
          "Connection: close\r\n\r\n",
          body,
        ],
        "")
  }
