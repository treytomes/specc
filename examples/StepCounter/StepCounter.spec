program: StepCounter

variable:
  name: n
  type: int
  source: stdin

while:
  variable: n
  condition: ne
  value: 0

print: "{n}"

branch:
  condition: default
  true_assign:
    target: n
    op: mul
    left: {n}
    right: 1
  true_assign:
    target: n
    op: sub
    left: {n}
    right: 1
