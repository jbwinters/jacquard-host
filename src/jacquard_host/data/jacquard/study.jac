-- First-party declarations for the synthetic study persistence operation
-- (roadmap H1.2). The host implements `save-response`; Jacquard decides what
-- to store and what each outcome means. Nothing here names a participant:
-- `session` is a synthetic token minted by the application.
type SaveOutcome = | Saved Text | Duplicate Text | Conflict

once effect Study where {
  save-response : (Text, Text, Text) -> SaveOutcome
}

record-response : (Text, Text, Text) ->{Study} Text
record-response(trial, session, answer) =
  match save-response(trial, session, answer) {
    | Saved(receipt) -> text.concat("saved:", receipt)
    | Duplicate(receipt) -> text.concat("duplicate:", receipt)
    | Conflict -> "conflict"
  }
