-- strtol parser edges; accepted cases use tiny delays so nothing stays pending
local cases = {
  {arg = "0",      accept = true,  due = "0"},
  {arg = "1",      accept = true,  due = "1"},
  {arg = "007",    accept = true,  due = "7"},    -- leading zeros fold
  {arg = "+12",    accept = true,  due = "12"},   -- unary plus accepted
  {arg = "-5",     accept = false},               -- negative rejected
  {arg = "12a",    accept = false},               -- trailing garbage
  {arg = "12.5",   accept = false},               -- stops at '.'
  {arg = "0x10",   accept = false},               -- base 10 stops at 'x'
  {arg = string.rep("9", 25), accept = false},    -- ERANGE overflow
}
for i, c in ipairs(cases) do
  rtt.send("async1 "..c.arg)
  if c.accept then
    rtt.expect("async1 #%d+ accepted, due in "..c.due.." ms", 500)
    rtt.expect("async1 #%d+ done", 2000)
  else
    rtt.expect("bad ms", 500)
  end
end
rtt.log("t15 PASS: "..#cases.." parser edge cases")
