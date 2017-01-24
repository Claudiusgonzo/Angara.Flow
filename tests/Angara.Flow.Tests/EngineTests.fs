﻿module ``Engine tests``

open NUnit.Framework

open Angara.Graph
open Angara.States
open Angara.Execution
open System.Diagnostics
open System.Threading
open System
open System.Reactive.Linq


type Dag = DirectedAcyclicGraph<Method, Edge<Method>>

type MethodMakeValue<'a>(value: 'a) =
    inherit Method(System.Guid.NewGuid(), [], [typeof<'a>])

    override x.Execute(_, _) =
        seq{ yield [box value], null }

type MethodOp<'a,'b>(f: 'a->'b) =
    inherit Method(System.Guid.NewGuid(), [typeof<'a>], [typeof<'b>])

    override x.Execute(args, _) =
        match args with
        | [a] -> 
            let value : 'a = unbox a
            seq{ yield [box (f value)], null }
        | _ -> failwith "Incorrect number of arguments"


let pickFinal states =
    let isFinal (update : StateUpdate<Method,MethodOutput>) =
        match 
            update.State.Vertices 
            |> Map.toSeq 
            |> Seq.choose(fun (_,vs) -> 
                match vs.AsScalar().Status with
                | VertexStatus.Incomplete (IncompleteReason.ExecutionFailed e) -> Some e
                | _ -> None)
            |> Seq.toList with
        | [] ->
            update.State.Vertices |> Map.forall(fun _ vs -> 
                match vs.AsScalar().Status with
                | VertexStatus.Final _ -> true
                | _ -> false)
        | errors -> failwithf "Some methods failed: %A" errors

    Observable.FirstAsync(states, new Func<StateUpdate<Method,MethodOutput>, bool>(isFinal))
    |> Observable.map(fun update -> update.State)

let output (m:Method, output:OutputRef) (state:State<Method,MethodOutput>) =
    unbox (state.Vertices.[m].AsScalar().Data.Value.Output.TryGet(output).Value)

let runToCompletion state =
    use engine = new Engine(state, Scheduler.ThreadPool())
    let final = pickFinal engine.Changes   
    engine.Start()
    final.GetAwaiter().GetResult()


[<Test; Category("CI")>]
let ``Engine executes a flow containing a single method`` () =
    let methodOne : Method = upcast MethodMakeValue System.Math.PI
    let graph = 
        FlowGraph.empty()
        |> FlowGraph.add methodOne

    // The initial state has no value for the method. 
    // It must be built automatically by the engine when it is started.
    // Automatic initial state of the method will be CanStart.
    let vertices = VerticesState([])

    let state = 
        { TimeIndex = 0UL
          Graph = graph
          Vertices = vertices          
        }
    
    let s = runToCompletion state
    let result : float = s |> output (methodOne,0)
    Assert.AreEqual(System.Math.PI, result, "Execution result")


[<Test; Category("CI")>]
let ``Engine executes a flow containing a chain of two methods`` () =
    let methodOne : Method = upcast MethodMakeValue<float> System.Math.PI
    let methodInc : Method = upcast MethodOp<float,float> (fun a -> a + 1.0)

    let graph = 
        FlowGraph.empty()
        |> FlowGraph.add methodOne
        |> FlowGraph.add methodInc
        |> FlowGraph.connect (methodOne, 0) (methodInc, 0)

    // The initial state has no value for the method. 
    // It must be built automatically by the engine when it is started.
    // Automatic initial state of the method will be CanStart.
    let vertices = VerticesState([])

    let state = 
        { TimeIndex = 0UL
          Graph = graph
          Vertices = vertices          
        }
        
    let s = runToCompletion state
    let result : float = s |> output (methodInc,0)
    Assert.AreEqual(System.Math.PI + 1.0, result, "Execution result")