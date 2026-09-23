-- quiet rejection prints nothing and must not consume an id
rtt.send("async2 1500")
rtt.send("async2 quiet")   -- back to back on purpose: must reject silently
rtt.send("tick")
rtt.expect("tick: off", 500)   -- probe: console alive
local r1 = rtt.expect("async2 #%d+ done", 3000)
local a = tonumber(r1:match("async2 #(%d+) done"))
rtt.send("async2 50")
local r2 = rtt.expect("async2 #%d+ done", 2000)
local b = tonumber(r2:match("async2 #(%d+) done"))
assert(b == a + 1, "FAIL: rejected request consumed id: "..a.." -> "..b)
rtt.log("t07 PASS (ids "..a.."->"..b..")")
