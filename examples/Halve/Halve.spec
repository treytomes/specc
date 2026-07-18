program: Halve

variable:
  name: n
  type: int
  source: stdin

while:
  variable: n
  condition: ne
  value: 1

print: "{n}"

branch:
  condition: default
  true_assign:
    target: n
    op: div
    left: {n}
    right: 2
