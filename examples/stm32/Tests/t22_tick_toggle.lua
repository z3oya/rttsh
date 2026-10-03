-- set replies carry no colon ("tick on"); the query does ("tick: off").
-- Leaves tick off, which every other script assumes as a precondition.
rtt.send("tick on")
rtt.expect("tick on", 500)
local _, _, t1 = rtt.expect("tick=(%d+)", 2500)
local _, _, t2 = rtt.expect("tick=(%d+)", 2500)
assert(tonumber(t2) == tonumber(t1) + 1,
  "FAIL: uptime did not advance 1 per second: "..t1.." -> "..t2)
rtt.send("tick off")
rtt.expect("tick off", 500)
-- after 'tick off' the once-a-second tick must stay quiet for a full window
rtt.expect_absent("tick=%d+", 1500)
rtt.send("tick")
rtt.expect("tick: off", 500)
rtt.log("t22 PASS (uptime "..t1.."->"..t2..")")
