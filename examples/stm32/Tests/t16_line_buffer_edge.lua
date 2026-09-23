-- 63 chars survive whole; 64 truncate to 63, welding the arg to the tail
rtt.send(string.rep("a", 63))
rtt.expect("not found", 500)
rtt.send(string.rep("a", 70))
rtt.expect("not found", 500)
rtt.send("async1 "..string.rep("1", 56))   -- "async1 " + 56 digits -> ERANGE
rtt.expect("bad ms", 500)
rtt.send("tick on"..string.rep("x", 57))   -- 64 chars -> truncated at 63
rtt.expect("usage: tick", 500)
rtt.log("t16 PASS: 63-char line buffer edges")
