rtt.send("help")
rtt.expect("async1 - ", 500)
rtt.expect("async2 - ", 500)
rtt.expect("silent accept", 500)
-- pcall also fails on a malformed pattern, so assert the timeout text itself
local ok, err = pcall(function() rtt.expect("block", 300) end)
assert(not ok and tostring(err):find("not found", 1, true),
  "FAIL: expected no 'block' in help, got: "..tostring(err))
rtt.log("t00 PASS")
