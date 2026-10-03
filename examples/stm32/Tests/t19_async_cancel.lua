rtt.send("async1 5000")
local _, _, id = rtt.expect("async1 #(%d+) accepted", 500)
rtt.send("async1 cancel")
rtt.expect("async1 #"..id.." cancelled", 500)
rtt.send("async1 100")   -- slot must be free right away
local _, _, id2 = rtt.expect("async1 #(%d+) accepted, due in 100 ms", 500)
assert(tonumber(id2) == tonumber(id) + 1,
  "FAIL: id not consecutive after cancel: "..id.." -> "..id2)
rtt.expect("async1 #"..id2.." done", 2000)
rtt.send("async1 cancel")
rtt.expect("async1 cancel: nothing pending", 500)
rtt.send("async2 cancel")
rtt.expect("async2 cancel: nothing pending", 500)
rtt.log("t19 PASS (ids "..id.."->"..id2..")")
