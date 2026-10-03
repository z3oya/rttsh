rtt.send("async1 300")
-- expect returns the text, the match, then the captures
local _, _, id = rtt.expect("async1 #(%d+) accepted, due in 300 ms", 500)
rtt.expect("async1 #"..id.." done", 2000)
rtt.log("t01 PASS (id="..id..")")
