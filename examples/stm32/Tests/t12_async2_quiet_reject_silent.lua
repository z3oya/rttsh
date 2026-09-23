rtt.send("async2 2500")
rtt.send("async2 quiet")   -- back to back on purpose: must reject silently
-- pcall also fails on a malformed pattern, so assert the timeout text itself
local busy, berr = pcall(function() rtt.expect("busy", 400) end)
assert(not busy and tostring(berr):find("not found", 1, true),
  "FAIL: expected no busy log, got: "..tostring(berr))
rtt.send("tick")
rtt.expect("tick: off", 500)   -- probe: console alive
rtt.expect("async2 #%d+ done", 3300)
rtt.log("t12 PASS")
