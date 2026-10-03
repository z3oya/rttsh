local delay = 1500
rtt.send("async1 "..delay)
local _, _, id = rtt.expect("async1 #(%d+) accepted", 500)
rtt.send_hex("7a 7a")   -- "zz" with no CR: line stays unsubmitted
-- window must outlast the armed delay, else the negative proof is vacuous
rtt.expect_absent("async1 #"..id.." done", delay + 300)
rtt.send_hex("0d")      -- CR submits the held line
rtt.expect("not found", 500)
rtt.expect("async1 #"..id.." done", 1000)
rtt.log("t13 PASS (id="..id..")")
