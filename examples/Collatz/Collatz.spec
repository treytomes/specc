program: Collatz

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
  condition: divisible_by_2
  divisor: 2
  true_assign:
    target: n
    op: div
    left: {n}
    right: 2

branch:
  condition: default
  true_assign:
    target: n
    op: mul
    left: {n}
    right: 3
  true_assign:
    target: n
    op: add
    left: {n}
    right: 1
