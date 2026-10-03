-- both sends precede any expect on purpose: that interleaving is under test
rtt.send("async2 1500")
rtt.send("async1 200")
local _, _, id = rtt.expect("async1 #(%d+) accepted", 500)
rtt.expect("async1 #"..id.." done", 2000)
rtt.expect("async2 #%d+ done", 3000)
rtt.log("t05 PASS (async1 id="..id..")")
