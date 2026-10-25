shipping-cost : (Int) ->{} Int
shipping-cost(total) = if lt(total, 50) then 5 else 0
