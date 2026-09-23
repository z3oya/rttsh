rtt.send("async2 2000")
-- pcall also fails on a malformed pattern, so assert the timeout text itself
local acc, aerr = pcall(function() rtt.expect("accepted", 400) end)
assert(not acc and tostring(aerr):find("not found", 1, true),
  "FAIL: expected no accept message, got: "..tostring(aerr))
local busy, berr = pcall(function() rtt.expect("busy", 100) end)
assert(not busy and tostring(berr):find("not found", 1, true),
  "FAIL: expected no busy message, got: "..tostring(berr))
rtt.expect("async2 #%d+ done", 3000)
rtt.log("t11 PASS")
