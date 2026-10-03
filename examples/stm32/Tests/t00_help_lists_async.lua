rtt.send("help")
rtt.expect("async1 - ", 500)
rtt.expect("async2 - ", 500)
rtt.expect("silent accept", 500)
-- 'block' must not appear in help: expect_absent succeeds on the quiet window
rtt.expect_absent("block", 300)
rtt.log("t00 PASS")
