-- The synthetic readability trial (roadmap H1.3): one deterministic trial,
-- one validated response, one receipt. Assembled after http.jac and
-- study.jac (Jacquard has no import syntax). This is an integration proof
-- with synthetic data only; it is not the readability study.
trial.id = "trial-synthetic-1"

trial.passage-a = "The committee approved the plan after a short discussion."

trial.passage-b = "Following a brief deliberative exchange, the committee's approval of the plan was granted."

trial.body =
  text.join-list(
    [
      "trial=", trial.id, "\n",
      "passage-a=", trial.passage-a, "\n",
      "passage-b=", trial.passage-b, "\n",
      "answer-choices=a,b\n",
    ],
    "")

trial.get(request) = http.text-response(200, trial.body)

-- form bodies: key=value pairs joined by "&"; values use the same token
-- alphabet the persistence operation accepts, so nothing needs decoding
trial.token-chars = http.chars("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")

trial.token?(s) =
  bool.and(
    bool.not(text.empty?(s)),
    http.all?(http.chars(s), fn (c) -> list.contains?(trial.token-chars, c, text.eq)))

trial.parse-pair(piece) =
  match http.split-once(piece, "=") {
    | None -> None
    | Some((key, value)) ->
      if bool.and(trial.token?(key), trial.token?(value)) then Some((key, value)) else None
  }

trial.parse-form(body) =
  if text.empty?(body) then Some([])
  else
    list.fold(text.split(body, "&"), Some([]), fn (acc, piece) ->
      match acc {
        | None -> None
        | Some(pairs) ->
          match trial.parse-pair(piece) {
            | None -> None
            | Some(pair) -> Some(list.append(pairs, [pair]))
          }
      })

trial.field(pairs, key) =
  match list.filter(pairs, fn (pair) -> { let (k, _) = pair
                                          text.eq?(k, key) }) {
    | Cons((_, value), Nil) -> Some(value)
    | _ -> None
  }

trial.answer?(answer) = bool.or(text.eq?(answer, "a"), text.eq?(answer, "b"))

trial.post(request) =
  match request {
    | HttpRequest(body: body) ->
      match trial.parse-form(body) {
        | None -> http.text-response(400, "body must be key=value pairs joined by &")
        | Some(pairs) ->
          match (trial.field(pairs, "trial"), trial.field(pairs, "session"), trial.field(pairs, "answer")) {
            | (Some(trial-id), Some(session), Some(answer)) ->
              if bool.not(text.eq?(trial-id, trial.id)) then http.text-response(404, "unknown trial")
              else if bool.not(trial.answer?(answer)) then http.text-response(400, "answer must be a or b")
              else match save-response(trial-id, session, answer) {
                | Saved(receipt) -> http.text-response(201, text.concat("receipt=", receipt))
                | Duplicate(receipt) -> http.text-response(200, text.concat("receipt=", receipt))
                | Conflict -> http.text-response(409, "a different answer is already recorded for this session")
              }
            | _ -> http.text-response(400, "trial, session, and answer are each required exactly once")
          }
      }
  }

trial.routes =
  [
    (HttpRoute(method: Get, path: "/trial"), fn (request) -> trial.get(request)),
    (HttpRoute(method: Post, path: "/response"), fn (request) -> trial.post(request)),
  ]

trial.serve : (Text) ->{Study} Text
trial.serve(raw) =
  match http.parse(raw) {
    | HttpBad(reject) -> http.render(http.reject-response(reject))
    | HttpOk(request) ->
      match http.route(request, trial.routes) {
        | Some(handler) -> http.render(handler(request))
        | None ->
          if http.path-known?(request, trial.routes) then http.render(http.text-response(405, "method not allowed"))
          else http.render(http.text-response(404, "not found"))
      }
  }
