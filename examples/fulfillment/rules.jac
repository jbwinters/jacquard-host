app.any(items, predicate) = match items {
  | Nil -> False
  | Cons(item, rest) -> if predicate(item) then True else app.any(rest, predicate)
}
app.empty?(items) = match items { | Nil -> True | Cons(_, _) -> False }

-- Synthetic inventory workflow. Python owns transactions; Jacquard owns policy.
type Line = | Line Text Int
type Order = | Order Text (List Line)
type Stock = | Stock Int | UnknownSku
type Reservation = | BookingPending | Booked Text | RepeatBooking Text | BookingConflict | StockChanged
type Fulfillment = | Fulfilled Text | DuplicateOrder Text | OrderRejected Text

once effect Inventory where {
  booking-status : (Order) -> Reservation
  lookup-stock : (Text) -> Stock
  reserve-order : (Order) -> Reservation
}
once effect Dispatch where {
  publish-booking : (Text, Text) -> ()
}

order.valid-lines(lines, seen) = match lines {
  | Nil -> True
  | Cons(Line(sku, units), rest) ->
    if bool.or(text.empty?(sku), bool.or(lt(units, 1), lt(20, units))) then False
    else if app.any(seen, fn (old) -> text.eq?(sku, old)) then False
    else order.valid-lines(rest, Cons(sku, seen))
}

order.shortage(lines) = match lines {
  | Nil -> None
  | Cons(Line(sku, units), rest) -> match lookup-stock(sku) {
    | UnknownSku -> Some(text.concat("unknown SKU: ", sku))
    | Stock(available) -> if lt(available, units) then Some(text.concat("short stock: ", sku))
                          else order.shortage(rest)
  }
}

fulfillment.place : (Order) ->{Inventory, Dispatch} Fulfillment
fulfillment.place(request) = match request {
  | Order(id, lines) ->
    if bool.or(text.empty?(id), bool.or(app.empty?(lines), bool.not(order.valid-lines(lines, []))))
    then OrderRejected("invalid order")
    else match booking-status(request) {
      | RepeatBooking(receipt) -> DuplicateOrder(receipt)
      | BookingConflict -> OrderRejected("idempotency conflict")
      | _ -> match order.shortage(lines) {
        | Some(reason) -> OrderRejected(reason)
        | None -> match reserve-order(request) {
          | Booked(receipt) -> { publish-booking(id, receipt); Fulfilled(receipt) }
          | RepeatBooking(receipt) -> DuplicateOrder(receipt)
          | BookingConflict -> OrderRejected("idempotency conflict")
          | StockChanged -> OrderRejected("stock changed before commit")
          | BookingPending -> OrderRejected("invalid reservation state")
        }
      }
    }
}
