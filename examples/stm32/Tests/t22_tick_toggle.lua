-- set replies carry no colon ("tick on"); the query does ("tick: off").
-- Leaves tick off, which every other script assumes as a precondition.
rtt.send("tick on")
rtt.expect("tick on", 500)
local t1 = tonumber(rtt.expect("tick=%d+", 2500):match("tick=(%d+)"))
local t2 = tonumber(rtt.expect("tick=%d+", 2500):match("tick=(%d+)"))
assert(t2 == t1 + 1, "FAIL: uptime did not advance 1 per second: "..t1.." -> "..t2)
rtt.send("tick off")
rtt.expect("tick off", 500)
-- pcall also fails on a malformed pattern, so assert the timeout text itself
local still, serr = pcall(function() rtt.expect("tick=%d+", 1500) end)
assert(not still and tostring(serr):find("not found", 1, true),
  "FAIL: tick kept printing after 'tick off': "..tostring(serr))
rtt.send("tick")
rtt.expect("tick: off", 500)
rtt.log("t22 PASS (uptime "..t1.."->"..t2..")")
