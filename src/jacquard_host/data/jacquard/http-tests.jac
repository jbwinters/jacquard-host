-- Warp tests for the HTTP/1.1 subset. Run against the concatenation of
-- http.jac and this file (Jacquard has no import syntax):
--   cat jacquard/http.jac jacquard/http-tests.jac > /tmp/http-all.jac
--   jacquard test /tmp/http-all.jac --seed 42
http-tests.status(parsed) =
  match parsed {
    | HttpOk(_) -> 200
    | HttpBad(HttpReject(status: status)) -> status
  }

http-tests.reason(parsed) =
  match parsed {
    | HttpOk(_) -> "ok"
    | HttpBad(HttpReject(reason: reason)) -> reason
  }

http-tests.body(parsed) =
  match parsed {
    | HttpOk(HttpRequest(body: body)) -> body
    | HttpBad(_) -> "<rejected>"
  }

http-tests.target(parsed) =
  match parsed {
    | HttpOk(HttpRequest(target: target)) -> target
    | HttpBad(_) -> "<rejected>"
  }

http-tests.get = "GET /trial?x=1 HTTP/1.1\r\nHost: localhost\r\nAccept: text/plain\r\n\r\n"

http-tests.post = "POST /response HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\n\r\nhello"

http-tests.repeat(s, n) = if int.lte?(n, 0) then "" else text.concat(s, http-tests.repeat(s, sub(n, 1)))

http-tests.expect-status(name, raw, status) =
  Case(name, fn () -> check.eq(http-tests.status(http.parse(raw)), status, int.eq, int.show, name))

http-tests.all =
  Group("http subset", [
    Group("accepted", [
      Case("GET with query", fn () -> {
        let parsed = http.parse(http-tests.get)
        check.eq(http-tests.status(parsed), 200, int.eq, int.show, "status")
        check.eq(http-tests.target(parsed), "/trial?x=1", text.eq, text.show, "target")
        check.eq(http.path("/trial?x=1"), "/trial", text.eq, text.show, "path")
        check.eq(http.query("/trial?x=1"), "x=1", text.eq, text.show, "query")
        check.eq(http-tests.body(parsed), "", text.eq, text.show, "empty body")
      }),
      Case("POST with Content-Length body", fn () -> {
        let parsed = http.parse(http-tests.post)
        check.eq(http-tests.status(parsed), 200, int.eq, int.show, "status")
        check.eq(http-tests.body(parsed), "hello", text.eq, text.show, "body")
      }),
      Case("header names are case-insensitive and values trimmed", fn () -> {
        match http.parse("GET / HTTP/1.1\r\nHOST:   example  \r\nX-Token: a\tb\r\n\r\n") {
          | HttpOk(request) -> {
            check.eq(option.with-default(http.header(request, "host"), "?"), "example", text.eq, text.show, "host")
            check.eq(option.with-default(http.header(request, "x-token"), "?"), "a\tb", text.eq, text.show, "tab inside a value")
            check.true(option.none?(http.header(request, "missing")), "absent header")
          }
          | HttpBad(_) -> check.true(False, "should parse")
        }
      }),
      Case("Content-Length zero on POST", fn () ->
        check.eq(http-tests.status(http.parse("POST /r HTTP/1.1\r\nHost: h\r\nContent-Length: 0\r\n\r\n")), 200, int.eq, int.show, "empty post")),
      Case("inputs exactly at each ceiling are accepted", fn () -> {
        let target = text.concat("/", http-tests.repeat("a", 511))
        check.eq(http-tests.status(http.parse(text.join-list(["GET ", target, " HTTP/1.1\r\nHost: h\r\n\r\n"], ""))), 200, int.eq, int.show, "512-byte target")
        check.eq(http-tests.status(http.parse(text.concat("GET / HTTP/1.1\r\nHost: h\r\n", text.concat(http-tests.repeat("X: y\r\n", 31), "\r\n")))), 200, int.eq, int.show, "32 header lines")
        check.eq(http-tests.status(http.parse(text.concat("GET / HTTP/1.1\r\nHost: h\r\nX: ", text.concat(http-tests.repeat("y", 1021), "\r\n\r\n")))), 200, int.eq, int.show, "1024-byte line")
        check.eq(text.length(http-tests.body(http.parse(text.concat("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 65536\r\n\r\n", http-tests.repeat("b", 65536))))), 65536, int.eq, int.show, "65536-byte body")
      }),
      Case("obs-text in a header value is kept and NBSP is not trimmed", fn () -> {
        match http.parse("GET / HTTP/1.1\r\nHost: h\r\nX: \xe9\xff\xa0\r\n\r\n") {
          | HttpOk(request) -> check.eq(option.with-default(http.header(request, "x"), "?"), "\xe9\xff\xa0", text.eq, text.show, "value")
          | HttpBad(_) -> check.true(False, "should parse")
        }
      }),
      Case("high bytes in a body are preserved", fn () -> {
        let body = "\xff\x00\x80\xfe"
        let parsed = http.parse(text.concat("POST /r HTTP/1.1\r\nHost: h\r\nContent-Length: 4\r\n\r\n", body))
        check.eq(http-tests.body(parsed), body, text.eq, text.show, "bytes")
      }),
    ]),
    Group("rejected", [
      http-tests.expect-status("no end of head", "GET / HTTP/1.1\r\nHost: h\r\n", 400),
      http-tests.expect-status("bare LF line endings", "GET / HTTP/1.1\nHost: h\n\n", 400),
      http-tests.expect-status("bare LF inside CRLF head", "GET / HTTP/1.1\r\nHost: h\nX: y\r\n\r\n", 400),
      http-tests.expect-status("bare CR inside a line", "GET / HTTP/1.1\r\nHost: h\rX: y\r\n\r\n", 400),
      http-tests.expect-status("obsolete line folding", "GET / HTTP/1.1\r\nHost: h\r\n continued\r\n\r\n", 400),
      http-tests.expect-status("missing Host", "GET / HTTP/1.1\r\nAccept: */*\r\n\r\n", 400),
      http-tests.expect-status("repeated Host", "GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", 400),
      http-tests.expect-status("HTTP/1.0", "GET / HTTP/1.0\r\nHost: h\r\n\r\n", 505),
      http-tests.expect-status("HTTP/2 preface", "PRI * HTTP/2.0\r\n\r\n", 505),
      http-tests.expect-status("unsupported method", "PUT / HTTP/1.1\r\nHost: h\r\n\r\n", 501),
      http-tests.expect-status("lowercase method", "get / HTTP/1.1\r\nHost: h\r\n\r\n", 501),
      http-tests.expect-status("absolute-form target", "GET http://h/ HTTP/1.1\r\nHost: h\r\n\r\n", 400),
      http-tests.expect-status("empty target", "GET  HTTP/1.1\r\nHost: h\r\n\r\n", 400),
      http-tests.expect-status("target with a control character", "GET /a\x01b HTTP/1.1\r\nHost: h\r\n\r\n", 400),
      http-tests.expect-status("extra request-line word", "GET / HTTP/1.1 x\r\nHost: h\r\n\r\n", 400),
      http-tests.expect-status("header without a colon", "GET / HTTP/1.1\r\nHost: h\r\nnocolon\r\n\r\n", 400),
      http-tests.expect-status("header name with a space", "GET / HTTP/1.1\r\nHost: h\r\nBad Name: v\r\n\r\n", 400),
      http-tests.expect-status("header value with a control character", "GET / HTTP/1.1\r\nHost: h\r\nX: a\x01b\r\n\r\n", 400),
      http-tests.expect-status("Transfer-Encoding", "POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n", 501),
      http-tests.expect-status("Transfer-Encoding beside Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 0\r\nTransfer-Encoding: chunked\r\n\r\n", 501),
      http-tests.expect-status("Upgrade", "GET / HTTP/1.1\r\nHost: h\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n", 501),
      http-tests.expect-status("Expect", "POST / HTTP/1.1\r\nHost: h\r\nExpect: 100-continue\r\nContent-Length: 0\r\n\r\n", 417),
      http-tests.expect-status("repeated Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\nx", 400),
      http-tests.expect-status("signed Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: +1\r\n\r\nx", 400),
      http-tests.expect-status("leading zero Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 01\r\n\r\nx", 400),
      http-tests.expect-status("empty Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length:\r\n\r\n", 400),
      http-tests.expect-status("huge Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 9999999999\r\n\r\n", 400),
      http-tests.expect-status("Content-Length above the body ceiling", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 65537\r\n\r\n", 413),
      http-tests.expect-status("body shorter than Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 5\r\n\r\nhell", 400),
      http-tests.expect-status("body longer than Content-Length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 5\r\n\r\nhello!", 400),
      http-tests.expect-status("body without Content-Length", "POST / HTTP/1.1\r\nHost: h\r\n\r\nhello", 400),
      http-tests.expect-status("GET with a body", "GET / HTTP/1.1\r\nHost: h\r\nContent-Length: 1\r\n\r\nx", 400),
      http-tests.expect-status("too many header lines", text.concat("GET / HTTP/1.1\r\nHost: h\r\n", text.concat(http-tests.repeat("X: y\r\n", 32), "\r\n")), 400),
      http-tests.expect-status("line too long", text.concat("GET / HTTP/1.1\r\nHost: h\r\nX: ", text.concat(http-tests.repeat("y", 1025), "\r\n\r\n")), 400),
      http-tests.expect-status("target too long", text.concat("GET /", text.concat(http-tests.repeat("a", 512), " HTTP/1.1\r\nHost: h\r\n\r\n")), 400),
    ]),
    Group("routing and rendering", [
      Case("route by method and path, distinguishing 404 from 405", fn () -> {
        let routes = [(HttpRoute(method: Get, path: "/trial"), 1), (HttpRoute(method: Post, path: "/response"), 2)]
        match http.parse(http-tests.get) {
          | HttpOk(request) -> {
            check.eq(option.with-default(http.route(request, routes), 0), 1, int.eq, int.show, "GET /trial")
            check.true(http.path-known?(request, routes), "known path")
          }
          | HttpBad(_) -> check.true(False, "should parse")
        }
        match http.parse("POST /trial HTTP/1.1\r\nHost: h\r\nContent-Length: 0\r\n\r\n") {
          | HttpOk(request) -> {
            check.true(option.none?(http.route(request, routes)), "POST /trial has no route")
            check.true(http.path-known?(request, routes), "but the path is known: 405")
          }
          | HttpBad(_) -> check.true(False, "should parse")
        }
        match http.parse("GET /nowhere HTTP/1.1\r\nHost: h\r\n\r\n") {
          | HttpOk(request) -> check.true(bool.not(http.path-known?(request, routes)), "unknown path: 404")
          | HttpBad(_) -> check.true(False, "should parse")
        }
      }),
      Case("render a response with Content-Length and Connection: close", fn () ->
        check.eq(
          http.render(http.text-response(201, "made")),
          "HTTP/1.1 201 Created\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 4\r\nConnection: close\r\n\r\nmade",
          text.eq, text.show, "rendered")),
      Case("render a rejection", fn () ->
        check.eq(
          http.render(http.reject-response(HttpReject(status: 505, reason: "only HTTP/1.1 is supported"))),
          "HTTP/1.1 505 HTTP Version Not Supported\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 26\r\nConnection: close\r\n\r\nonly HTTP/1.1 is supported",
          text.eq, text.show, "rendered")),
      Prop("a POST body of any accepted length round-trips", fn () -> {
        let n = sample(UniformInt(0, 300))
        let body = http-tests.repeat("z", n)
        let raw = text.join-list(["POST /r HTTP/1.1\r\nHost: h\r\nContent-Length: ", text.from-int(n), "\r\n\r\n", body], "")
        check.eq(http-tests.body(http.parse(raw)), body, text.eq, text.show, "body")
      }),
      Prop("rendering never emits a bare LF in its head and always ends with the body", fn () -> {
        let n = sample(UniformInt(0, 40))
        let body = http-tests.repeat("q", n)
        let rendered = http.render(http.text-response(200, body))
        let head = option.with-default(option.map(http.split-once(rendered, "\r\n\r\n"), fn (pair) -> { let (h, _) = pair
                                                                                                         h }), "")
        check.true(bool.not(text.contains?(text.join-list(text.split(head, "\r\n"), ""), "\n")), "no bare LF in the head")
        check.true(text.eq?(text.slice(rendered, sub(text.length(rendered), n), text.length(rendered)), body), "ends with body")
      }),
    ]),
  ])
