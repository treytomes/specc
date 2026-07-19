program: FizzBuzz

loop:
  from: 1
  to: 100

branch:
  condition: divisible_by_15
  divisor: 15
  true_output: "FizzBuzz"

branch:
  condition: divisible_by_3
  divisor: 3
  true_output: "Fizz"

branch:
  condition: divisible_by_5
  divisor: 5
  true_output: "Buzz"

branch:
  condition: default
  true_output: "{n}"

variable:
  name: n
  type: int
